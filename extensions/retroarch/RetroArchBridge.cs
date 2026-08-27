using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.RetroArch;

// RetroArch, over its own network-command interface.
//
// WHY THIS ONE IS WORTH BUILDING
// ──────────────────────────────
// Only one game in the catalogue needs it today (Gauntlet Legends, whose world
// says flatly "Retroarch is only emulator this client will accept"). But the
// transport is not core-specific: READ_CORE_RAM / WRITE_CORE_RAM go through
// whichever core is loaded, so this one bridge reaches every system RetroArch
// emulates. That is the opposite of the PINE or NWA bridges, which are tied to
// one emulator's one platform.
//
// THE PROTOCOL
// ────────────
// Read out of the world's own client (jamesbrq/GauntletLegendsAP,
// gl/GauntletLegendsClient.py) rather than from a wiki summary, because the
// exact spelling is the part a summary gets wrong -- RetroArch has BOTH
// READ_CORE_RAM (core-relative) and READ_CORE_MEMORY (system-bus), and picking
// the wrong one reads plausible rubbish from the wrong address space.
//
// A UDP socket on 127.0.0.1:55355. Plain ASCII, one datagram per message:
//
//   -> READ_CORE_RAM 0x64a68 12
//   <- READ_CORE_RAM 64a68 00 1f 04 ...          (space-separated hex bytes)
//
//   -> WRITE_CORE_RAM 0x3fc800 0x01 0x00 0x2A
//
//   -> GET_STATUS
//   <- GET_STATUS PLAYING mupen64plus_next,Gauntlet Legends,crc32=...
//
// ⚠ A reply containing "-1" means the core has no RAM at that address, or no
//   content is loaded at all. It is an ERROR, not data: the world's own client
//   raises on it, and so do we, because silently returning 0xFF bytes would
//   look exactly like a game that simply has not started yet.
public sealed class RetroArchBridge : IEmulatorBridge
{
    public const string Folder = "RetroArch";
    private const string Exe = "retroarch.exe";
    private const int CommandPort = 55355;

    public string   Protocol    => "retroarch";
    public string   DisplayName => "RetroArch";
    public string   HomepageUrl => "https://www.retroarch.com";

    /// The systems this bridge can start, and the libretro core it starts them
    /// with.
    ///
    /// ⚠ Deliberately short. RetroArch emulates far more than this, but a core
    /// name we have not read out of a real setup guide is a guess, and a guess
    /// here means the player gets "core not found" with no way to tell whether
    /// the bridge or their install is wrong. Entries get added when a game
    /// needs them and its world names the core.
    private static readonly IReadOnlyDictionary<string, string> Cores =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Gauntlet Legends' own client: cores/mupen64plus_next_libretro.dll
            ["N64"] = "mupen64plus_next_libretro.dll",
        };

    public string[] Systems => Cores.Keys.ToArray();

    /// ⚠ FALSE until someone has played a game through it.
    ///
    /// The protocol below is transcribed from the world's own client and the
    /// launcher builds the whole command line, but no check has yet crossed
    /// this wire. Saying true here would put Gauntlet Legends in the shop as a
    /// working game on the strength of code review alone.
    public bool IsReady => false;

    private static readonly LauncherV2.Core.Emulators.EmulatorSource RetroArchSource =
        new(Author:       "the RetroArch team (libretro)",
            Licence:      "GPL-3.0-or-later for RetroArch itself; each core "
                        + "carries its own licence",
            LicenceUrl:   "https://github.com/libretro/RetroArch/blob/master/COPYING",
            DownloadPage: "https://github.com/libretro/RetroArch/releases",
            Owner:        "libretro",
            Repo:         "RetroArch",
            AssetPattern: "RetroArch-Win64.7z");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "RetroArch", HomepageUrl, Exe, RetroArchSource),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        string? exe = Emulators[0].Resolve(root);
        if (exe is null)
            return "RetroArch is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
                 + $"directly in that folder. You can get it from {HomepageUrl}, or "
                 + "let the launcher fetch that same file from the libretro team's "
                 + "own release once you have seen who wrote it and under what licence.";

        // A RetroArch with no cores is a RetroArch that cannot open anything,
        // and the failure it produces ("Failed to load content") says nothing
        // about the missing file.
        string cores = Path.Combine(Path.GetDirectoryName(exe)!, "cores");
        if (!Directory.Exists(cores) || Directory.GetFiles(cores, "*_libretro.dll").Length == 0)
            return "RetroArch has no cores installed.\n\n"
                 + "Open RetroArch, go to Main Menu → Online Updater → Core "
                 + "Downloader, and install the core for the system you want to "
                 + "play. For Nintendo 64 that is Mupen64Plus-Next.";

        return null;
    }

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string dir = Path.GetDirectoryName(exe)!;
        if (!Cores.TryGetValue(context.RomSystem, out string? core)) return null;

        string corePath = Path.Combine(dir, "cores", core);
        if (!File.Exists(corePath)) return null;

        // ⚠⚠ THE NETWORK COMMANDS ARE OFF BY DEFAULT.
        //
        // Gauntlet Legends' own setup guide asks the player to do this by hand:
        // "Then in Settings -> Network, you must also turn On Network Commands."
        // Skip it and everything looks healthy -- RetroArch starts, the game
        // runs -- while every read times out and not one check is ever sent.
        // That is precisely the snes9x NWA trap, which cost a whole evening.
        EnableNetworkCommands(dir);

        // The N64 core needs CountPerOp=1 or the game's timing drifts far
        // enough that the client reads torn values. The world's client patches
        // the same file (config/Mupen64Plus-Next/Mupen64Plus-Next.opt).
        if (string.Equals(context.RomSystem, "N64", StringComparison.OrdinalIgnoreCase))
            SetCoreOption(dir, "Mupen64Plus-Next", "mupen64plus-CountPerOp", "1");

        string args = $"-L \"{corePath}\" \"{context.RomPath}\"";
        if (context.Fullscreen) args += " --fullscreen";

        return new LaunchPlan(exe, args, dir, new Dictionary<string, string>());
    }

    /// Turn on the UDP command interface in retroarch.cfg, preserving every
    /// other line the player has set.
    private static void EnableNetworkCommands(string dir)
    {
        try
        {
            string cfg = Path.Combine(dir, "retroarch.cfg");
            var lines = File.Exists(cfg)
                ? new List<string>(File.ReadAllLines(cfg))
                : new List<string>();

            Upsert(lines, "network_cmd_enable", "true");
            Upsert(lines, "network_cmd_port", CommandPort.ToString(CultureInfo.InvariantCulture));

            File.WriteAllLines(cfg, lines);
        }
        catch { /* best effort: the player can still flip it in Settings → Network */ }

        static void Upsert(List<string> lines, string key, string value)
        {
            string want = $"{key} = \"{value}\"";
            int at = lines.FindIndex(l => l.TrimStart().StartsWith(key + " ", StringComparison.Ordinal)
                                       || l.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
            if (at >= 0) lines[at] = want; else lines.Add(want);
        }
    }

    /// Set one core option in RetroArch's per-core override file.
    private static void SetCoreOption(string dir, string coreDir, string key, string value)
    {
        try
        {
            string folder = Path.Combine(dir, "config", coreDir);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, coreDir + ".opt");

            string content = File.Exists(path) ? File.ReadAllText(path) : "";
            string want = $"{key} = \"{value}\"";
            if (content.Contains(want, StringComparison.Ordinal)) return;

            content = content.Contains(key, StringComparison.Ordinal)
                ? Regex.Replace(content, Regex.Escape(key) + @"\s*=\s*""[^""]*""", want)
                : (content.Length == 0 ? want + "\n" : content.TrimEnd('\n') + "\n" + want + "\n");

            File.WriteAllText(path, content);
        }
        catch { /* best effort */ }
    }

    // ── the wire ────────────────────────────────────────────────────────────

    private UdpClient? _udp;

    public async Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var udp = new UdpClient();
        try
        {
            udp.Connect(IPAddress.Loopback, CommandPort);
            _udp = udp;

            // ⚠ A UDP socket never refuses, so "connected" has to mean the
            // emulator ANSWERED. GET_STATUS is the cheapest question that
            // proves both that RetroArch is up and that its command interface
            // is actually enabled.
            string? status = await AskAsync("GET_STATUS", ct).ConfigureAwait(false);
            if (status is null) { await DisconnectAsync().ConfigureAwait(false); return false; }

            // "GET_STATUS CONTENTLESS" means RetroArch is running with nothing
            // loaded -- every read would come back -1.
            return status.Contains("PLAYING", StringComparison.OrdinalIgnoreCase)
                || status.Contains("PAUSED", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            udp.Dispose();
            _udp = null;
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        _udp?.Dispose();
        _udp = null;
        return Task.CompletedTask;
    }

    public async Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
    {
        string? reply = await AskAsync(
            $"READ_CORE_RAM {Hex(address)} {length.ToString(CultureInfo.InvariantCulture)}",
            ct).ConfigureAwait(false);
        if (reply is null)
            throw new IOException("RetroArch did not answer a memory read.");

        // "READ_CORE_RAM <addr> <hh> <hh> ..." -- the first two fields are the
        // echoed command and address.
        string[] parts = reply.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            throw new IOException($"RetroArch gave a short read reply: {reply.Trim()}");

        var bytes = new byte[parts.Length - 2];
        for (int i = 2; i < parts.Length; i++)
        {
            if (parts[i] == "-1")
                throw new IOException(
                    $"RetroArch has no memory at 0x{address:x}. Either no content is "
                    + "loaded, or this core does not expose that address.");
            bytes[i - 2] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    public async Task WriteAsync(long address, byte[] data, CancellationToken ct)
    {
        var sb = new StringBuilder("WRITE_CORE_RAM ").Append(Hex(address));
        foreach (byte b in data) sb.Append(" 0x").Append(b.ToString("X2", CultureInfo.InvariantCulture));

        var udp = _udp ?? throw new InvalidOperationException("RetroArch is not connected.");
        byte[] msg = Encoding.ASCII.GetBytes(sb.ToString());
        await udp.SendAsync(msg.AsMemory(), ct).ConfigureAwait(false);
        // RetroArch acknowledges writes, but the world's own client does not
        // wait for it -- and waiting would halve the write rate for no gain.
    }

    /// Send one command and wait briefly for the datagram that answers it.
    private async Task<string?> AskAsync(string command, CancellationToken ct)
    {
        var udp = _udp;
        if (udp is null) return null;

        byte[] msg = Encoding.ASCII.GetBytes(command);
        await udp.SendAsync(msg.AsMemory(), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(result.Buffer);
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
    }

    /// Lowercase, 0x-prefixed -- the spelling the world's own client sends.
    private static string Hex(long address)
        => "0x" + address.ToString("x", CultureInfo.InvariantCulture);
}
