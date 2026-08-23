using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Pine;

// The PCSX2 bridge, shipped as an installable extension.
//
// PlayStation 2 was the last big hole in the catalogue: 344 games and not one
// of them a PS2 title, because nothing here could read that emulator's memory.
// PCSX2 has answered the door the whole time -- it carries PINE, its own
// interface for exactly this, and no in-emulator script is needed.
//
// ⛔ This extension carries PROTOCOL ONLY. No copy of PCSX2 is inside it, and
//    none is ever fetched by this file: it DECLARES an EmulatorSource and the
//    shared installer does the rest, after showing the player whose program it
//    is and under what licence.
//
// THE PROTOCOL
// ────────────
// Read out of PCSX2's own pcsx2/PINE.cpp rather than from a summary, because
// the framing is the part a summary always leaves out.
//
// A TCP socket on 127.0.0.1, default slot 28011. Every message -- in both
// directions -- opens with a little-endian u32 giving the TOTAL length
// INCLUDING those four bytes. Requests then hold one or more commands back to
// back; the reply holds one status byte and then each command's result in the
// same order.
//
//   request:  [u32 len][cmd][cmd]...
//   command:  [u8 opcode][args...]
//   reply:    [u32 len][u8 code][result][result]...
//
//   MsgRead8  = 0   args: u32 address            -> 1 byte
//   MsgRead16 = 1   MsgRead32 = 2   MsgRead64 = 3
//   MsgWrite8 = 4   args: u32 address, u8 value  -> nothing
//   MsgWrite16= 5   MsgWrite32 = 6  MsgWrite64 = 7
//   MsgVersion= 8   MsgTitle = 0xB  MsgID = 0xC  MsgStatus = 0xF
//
//   code 0x00 = OK, 0xFF = failed.
//
// ⭐ Commands BATCH. That is what makes this usable: a 256-byte read is 256
// MsgRead8 commands in one packet and one round trip, not 256 of them. The
// emulator's own limits are 650000 bytes in and 450000 out, so the chunk size
// below is far inside what it accepts.
public sealed class PineBridge : IEmulatorBridge
{
    /// PCSX2's default PINE slot -- literally PINE_DEFAULT_SLOT in PINE.h.
    private const int DefaultSlot = 28011;
    private const int ProbeTimeoutMs = 1500;

    /// Bytes per round trip. Chosen well under PCSX2's 450000-byte reply cap so
    /// a large read cannot trip its own safety check.
    private const int ChunkBytes = 4096;

    private const byte MsgRead8   = 0x00;
    private const byte MsgWrite8  = 0x04;
    private const byte MsgVersion = 0x08;
    private const byte MsgTitle   = 0x0B;
    private const byte MsgStatus  = 0x0F;
    private const byte IpcOk      = 0x00;

    /// PCSX2's EmuStatus, straight from the header.
    private const uint StatusRunning = 0, StatusPaused = 1, StatusShutdown = 2;

    public string   Protocol    => "pine";
    public string   DisplayName => "PCSX2 (PINE)";
    public string[] Systems     => new[] { "PS2" };
    public string   HomepageUrl => "https://pcsx2.net";

    /// PCSX2 itself. LGPL-3.0-or-later since the 2.x line; earlier releases
    /// were GPL-2.0. The player is told which, because that is what they are
    /// agreeing to when they accept an offer to fetch it.
    private static readonly LauncherV2.Core.Emulators.EmulatorSource Pcsx2Source =
        new(Author:       "the PCSX2 team",
            Licence:      "LGPL-3.0-or-later",
            LicenceUrl:   "https://github.com/PCSX2/pcsx2/blob/master/COPYING.LGPL",
            DownloadPage: "https://github.com/PCSX2/pcsx2/releases",
            Owner:        "PCSX2",
            Repo:         "pcsx2",
            AssetPattern: "windows-x64-Qt.7z");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            "PCSX2", "PCSX2 (PlayStation 2)",
            "https://pcsx2.net", "pcsx2-qt.exe", Pcsx2Source),
    };

    // TRUE since 23 August 2026, and here is what earned it.
    //
    // Proven against PCSX2 v2.6.3 with Need for Speed: Underground 2 running
    // from a CHD — the emulator answered MsgTitle with the game's own name, and
    // the bridge then read 256 bytes of live EE memory, wrote a pattern over
    // them, read it back byte for byte, and restored the original. A 5000-byte
    // read exercised the chunking loop against the real emulator rather than
    // the stand-in.
    //
    // The flag was false while only the stand-in had passed, because this
    // project has already paid for the other answer: the SNI bridge was
    // written, listed in the menu, and threw on Play because nobody had run
    // the whole chain. Working code is not a working bridge.
    //
    // ⚠ What is still NOT proven: carrying an actual Archipelago check. That
    // needs a PS2 world with a RAM map, and none of ours has one yet. This flag
    // says the transport works, not that any game is playable.
    public bool IsReady => true;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ── readiness ─────────────────────────────────────────────────────────────

    /// A PS2 BIOS the player dumped from their own console, or null.
    ///
    /// ⛔ We never ship one, never fetch one, and never link to a site that
    /// hands them out. It is Sony's firmware: PCSX2's own documentation says
    /// "these files are proprietary software and must therefore be dumped from
    /// your own console", and names no download as an alternative. Pointing a
    /// player at a BIOS site would make us the signpost to infringement, which
    /// is the same line this project holds for ROMs.
    ///
    /// So the launcher does the one useful thing it legitimately can: it says
    /// exactly what is missing, exactly where to put it, and how it is
    /// obtained — instead of starting an emulator that will fail with a blank
    /// screen and no reason.
    /// Where London keeps the programs the player installed. GetUnmetRequirement
    /// is handed no context by the interface, so the default root is derived the
    /// same way the launcher derives it — Path.Combine(AppContext.BaseDirectory,
    /// "Emulators"), read out of MainWindow rather than assumed.
    private static string DefaultEmulatorsRoot
        => Path.Combine(AppContext.BaseDirectory, "Emulators");

    public static string? FindBios(string emulatorsRoot)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(emulatorsRoot, "PCSX2", "bios"));
            if (!dir.Exists) return null;
            foreach (var f in dir.EnumerateFiles())
            {
                // A dumped BIOS is a ~4 MB image. The extension varies by the
                // tool that produced it, so size is the honest test — a stray
                // text file in the folder must not read as "you are all set".
                if (f.Length >= 3_000_000 && f.Length <= 8_000_000)
                    return f.FullName;
            }
        }
        catch { }
        return null;
    }

    /// What to tell a player who has no BIOS. Written out here because it is
    /// the whole value this bridge can add at that moment.
    public static string BiosMissingMessage(string emulatorsRoot)
        => "PCSX2 cannot start a PlayStation 2 game without a BIOS, and there "
         + "is none installed.\n\n"
         + "The BIOS is the console's own firmware. It is Sony's, it is not "
         + "part of PCSX2, and it is not something this launcher can give you "
         + "or point you to — PCSX2's own documentation says it \"must be "
         + "dumped from your own console\".\n\n"
         + "If you own a PlayStation 2, dumping it is a one-off job: PCSX2's "
         + "setup guide walks through it with a homebrew tool.\n\n"
         + "When you have the file, put it here:\n"
         + $"  {Path.Combine(emulatorsRoot, "PCSX2", "bios")}\n\n"
         + "It is a single file of roughly 4 MB. Then press Play again.";

    public string? GetUnmetRequirement()
    {
        // The BIOS first. Without it PCSX2 loads, answers PINE, and then fails
        // to boot the disc with nothing on screen to explain why — so the
        // launcher must say it before the player watches that happen.
        if (FindBios(DefaultEmulatorsRoot) is null)
            return BiosMissingMessage(DefaultEmulatorsRoot);

        uint? status;
        try
        {
            status = ProbeStatusAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return "PCSX2 could not be reached.\n\n"
                 + $"({ex.GetType().Name}: {ex.Message})\n\n"
                 + "Start PCSX2 and try again.";
        }

        if (status is null)
            return "PCSX2 is not answering on its PINE interface.\n\n"
                 + "Two things have to be true, and the second one is off by "
                 + "default:\n\n"
                 + "  1. PCSX2 is running.\n"
                 + "  2. PINE is enabled — Settings ▸ Advanced ▸ "
                 + $"Enable PINE, slot {DefaultSlot}.\n\n"
                 + "PCSX2 gives no warning when PINE is off; it simply never "
                 + "answers.";

        if (status == StatusShutdown)
            return "PCSX2 is running but no game is loaded.\n\n"
                 + "Start the game first, then press Play here.";

        // Paused is fine. The game is loaded and memory is readable; it will
        // resume the moment the player unpauses, and refusing here would be a
        // rule of ours, not the emulator's.
        return null;
    }

    /// PCSX2 is started by London like any other emulator, with the disc image
    /// this session is for. PINE itself is a setting inside PCSX2, not a
    /// command line switch, which is why GetUnmetRequirement explains it.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        var args = new StringBuilder();
        if (context.Fullscreen) args.Append("-fullscreen ");
        args.Append('"').Append(context.RomPath).Append('"');

        return new LaunchPlan(exe, args.ToString(),
                              Path.GetDirectoryName(exe) ?? emulatorsRoot);
    }

    // ── connection ────────────────────────────────────────────────────────────

    /// The emulator's status, or null when PINE is not answering at all. Those
    /// are two different problems for the player and must not collapse into one
    /// message.
    private static async Task<uint?> ProbeStatusAsync()
    {
        using var cts = new CancellationTokenSource(ProbeTimeoutMs);
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync("127.0.0.1", DefaultSlot, cts.Token)
                       .ConfigureAwait(false);
            var stream = probe.GetStream();
            byte[] reply = await ExchangeAsync(
                stream, new[] { new byte[] { MsgStatus } }, cts.Token)
                .ConfigureAwait(false);
            return reply.Length >= 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(reply)
                : StatusShutdown;
        }
        catch (SocketException)          { return null; }
        catch (OperationCanceledException) { return null; }
        catch (IOException)              { return null; }
    }

    public async Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", DefaultSlot, ct)
                        .ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();

            // Ask something real before reporting success. A socket that
            // accepts is not the same as an emulator that will answer, and
            // "connected" has meant the wrong thing here before.
            byte[] status = await ExchangeAsync(
                _stream, new[] { new byte[] { MsgStatus } }, ct).ConfigureAwait(false);
            if (status.Length < 4) { await DisconnectAsync().ConfigureAwait(false); return false; }

            return BinaryPrimitives.ReadUInt32LittleEndian(status) != StatusShutdown;
        }
        catch (Exception)
        {
            client.Dispose();
            _client = null;
            _stream = null;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        await Task.CompletedTask;
    }

    // ── memory ────────────────────────────────────────────────────────────────

    public async Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
    {
        if (length <= 0) return Array.Empty<byte>();
        var stream = _stream ?? throw new InvalidOperationException("not connected");

        var outBuf = new byte[length];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int done = 0; done < length; done += ChunkBytes)
            {
                int take = Math.Min(ChunkBytes, length - done);
                var cmds = new List<byte[]>(take);
                for (int i = 0; i < take; i++)
                {
                    var c = new byte[5];
                    c[0] = MsgRead8;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        c.AsSpan(1), (uint)(address + done + i));
                    cmds.Add(c);
                }

                byte[] reply = await ExchangeAsync(stream, cmds, ct).ConfigureAwait(false);
                if (reply.Length < take)
                    throw new IOException(
                        $"PCSX2 returned {reply.Length} bytes for a {take}-byte read");
                Array.Copy(reply, 0, outBuf, done, take);
            }
        }
        finally { _gate.Release(); }
        return outBuf;
    }

    public async Task WriteAsync(long address, byte[] data, CancellationToken ct)
    {
        if (data.Length == 0) return;
        var stream = _stream ?? throw new InvalidOperationException("not connected");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int done = 0; done < data.Length; done += ChunkBytes)
            {
                int take = Math.Min(ChunkBytes, data.Length - done);
                var cmds = new List<byte[]>(take);
                for (int i = 0; i < take; i++)
                {
                    var c = new byte[6];
                    c[0] = MsgWrite8;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        c.AsSpan(1), (uint)(address + done + i));
                    c[5] = data[done + i];
                    cmds.Add(c);
                }
                await ExchangeAsync(stream, cmds, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    // ── framing ───────────────────────────────────────────────────────────────

    /// One request, one reply, with the status byte checked and stripped.
    /// Returns the concatenated results of the commands, in order.
    private static async Task<byte[]> ExchangeAsync(
        NetworkStream stream, IReadOnlyList<byte[]> commands, CancellationToken ct)
    {
        int payload = 0;
        foreach (var c in commands) payload += c.Length;

        // ⚠ The length counts ITSELF. Sending the payload length instead makes
        // PCSX2 wait for four more bytes that never come, and the call simply
        // hangs -- no error, no reply.
        var request = new byte[4 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
        int at = 4;
        foreach (var c in commands) { c.CopyTo(request, at); at += c.Length; }

        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        var header = new byte[4];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (total < 5)
            throw new IOException($"PCSX2 replied with a {total}-byte message");

        var rest = new byte[total - 4];
        await ReadExactlyAsync(stream, rest, ct).ConfigureAwait(false);
        if (rest[0] != IpcOk)
            throw new IOException(
                "PCSX2 rejected the request (PINE code 0x"
                + rest[0].ToString("X2") + "). The usual cause is an address "
                + "outside the game's memory.");

        var body = new byte[rest.Length - 1];
        Array.Copy(rest, 1, body, 0, body.Length);
        return body;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("PCSX2 closed the connection");
            got += n;
        }
    }
}
