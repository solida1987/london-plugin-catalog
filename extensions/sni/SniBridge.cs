using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Sni;

// The SNI bridge, shipped as an installable extension.
//
// Every SNES world in Archipelago subclasses SNIClient, which talks to SNI --
// a separate program that bridges to snes9x, bsnes or real hardware. That is a
// different conversation from BizHawk's in-emulator Lua script, so it lives
// out here instead of being compiled into the launcher.
//
// ⛔ This extension carries PROTOCOL ONLY -- no copy of SNI or of any emulator
//    is inside it. The player installs both themselves, or accepts an offer to
//    fetch them from their own projects' releases after being shown who wrote
//    them and under what licence. The extension does none of that fetching: it
//    only DECLARES an EmulatorSource, and Tools/lint_emulator_source.py holds
//    Core/Extensions to exactly that.
//
// THE PROTOCOL
// ────────────
// SNI keeps a usb2snes-compatible WebSocket interface on port 23074, and that
// is what Archipelago's own SNIClient uses -- read out of the shipped client
// itself, which carries "ws://", 23074, and the four opcodes below. SNI also
// speaks gRPC on 8191, and an earlier version of this file probed THAT port to
// decide whether SNI was running: the wrong question, answered confidently.
//
// usb2snes is JSON control frames with binary payloads:
//   {"Opcode":"DeviceList","Space":"SNES"}              -> {"Results":[names]}
//   {"Opcode":"Attach","Space":"SNES","Operands":[name]} (no reply)
//   {"Opcode":"GetAddress",...,"Operands":[addr,size]}  -> size bytes, any framing
//   {"Opcode":"PutAddress",...,"Operands":[addr,size]}  then size bytes
// Addresses are hex WITHOUT 0x, in SNI's flat space (WRAM starts at 0xF50000).
public sealed class SniBridge : IEmulatorBridge
{
    // The usb2snes-compatible port. NOT 8191 -- that is SNI's gRPC service,
    // which this bridge does not speak.
    private const int DefaultPort = 23074;
    private const int ProbeTimeoutMs = 1500;

    public string   Protocol    => "sni";
    public string   DisplayName => "SNI (Super Nintendo Interface)";
    public string[] Systems     => new[] { "SNES" };
    public string   HomepageUrl => "https://github.com/alttpo/sni";

    /// SNI itself: a small Go program that speaks to SNES emulators and real
    /// hardware. MIT, and the licence really is just MIT.
    private static readonly LauncherV2.Core.Emulators.EmulatorSource SniSource =
        new(Author:      "jsd1982 and the SNI contributors (alttpo)",
            Licence:     "MIT",
            LicenceUrl:  "https://github.com/alttpo/sni/blob/main/LICENSE",
            DownloadPage:"https://github.com/alttpo/sni/releases",
            Owner:       "alttpo",
            Repo:        "sni",
            AssetPattern:"windows-amd64.zip");

    /// snes9x is NOT free software, and the dialog has to say so in the line
    /// the player actually reads. Its licence grants personal, non-commercial
    /// use only -- fine for someone playing a multiworld, and exactly the sort
    /// of condition that must be on screen before anything is fetched rather
    /// than discovered afterwards in a file.
    ///
    /// ⚠ The build matters as much as the licence. This points at Skarsnik's
    /// snes9x-emunwa FORK, not snes9xgit's stock snes9x: only the fork carries
    /// the Network Access server that both SNI and the launcher's own NWA path
    /// talk to. An earlier declaration here named the stock project -- a
    /// download that installs cleanly and can never answer.
    private static readonly LauncherV2.Core.Emulators.EmulatorSource Snes9xSource =
        new(Author:      "the Snes9x team (Gary Henderson, Jerremy Koot, BearOso, "
                       + "OV2 and many others); NWA build by Sylvain Colinet (Skarsnik)",
            Licence:     "Snes9x licence — free for personal, NON-COMMERCIAL use only",
            LicenceUrl:  "https://github.com/Skarsnik/snes9x-emunwa/blob/master/LICENSE",
            DownloadPage:"https://github.com/Skarsnik/snes9x-emunwa/releases",
            Owner:       "Skarsnik",
            Repo:        "snes9x-emunwa",
            AssetPattern:"nwa-win32-x64.7z");

    /// SNI is a bridge, not an emulator: it needs BOTH itself and something to
    /// talk to. London creates a folder and a note for each. The player puts
    /// their own copy in, or accepts an offer to fetch that same file from the
    /// project's own release -- we never carry a copy of either program.
    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            "SNI", "SNI (Super Nintendo Interface)",
            "https://github.com/alttpo/sni", "sni.exe", SniSource),
        new EmulatorRequirement(
            "snes9x", "snes9x (NWA build)",
            "https://github.com/Skarsnik/snes9x-emunwa/releases", "snes9x-x64.exe",
            Snes9xSource),
    };

    /// The transport is implemented. Whether it can be USED right now is a
    /// separate question, and GetUnmetRequirement answers that one -- SNI has
    /// to be running with a device attached, and neither is our doing.
    public bool IsReady => true;

    private ClientWebSocket? _socket;
    private string? _device;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? GetUnmetRequirement()
    {
        string[]? devices;
        try
        {
            devices = ProbeDevicesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return "SNI could not be reached.\n\n"
                 + $"({ex.GetType().Name}: {ex.Message})\n\n"
                 // No longer "never downloads it for you": since 3.7.15 the
                 // launcher offers to fetch SNI from its own release after
                 // showing the author and the licence. Saying otherwise sent
                 // the player looking for a manual route they did not need.
                 + "Start SNI, then try again. If you do not have it, the "
                 + "button next to Play offers to install it, or get it "
                 + $"yourself from {HomepageUrl}.";
        }

        if (devices is null)
            return "SNI does not appear to be running.\n\n"
                 + "Start SNI and connect it to your emulator, then try again. "
                 + $"You can get SNI from {HomepageUrl} — this launcher never "
                 + "downloads it for you.";

        if (devices.Length == 0)
            return "SNI is running, but no emulator or console is attached to it.\n\n"
                 + "Open your SNES emulator and load the game, then check that "
                 + "SNI lists it as a device.";

        return null;
    }

    /// Null on purpose: SNI attaches to an emulator the PLAYER starts, so there
    /// is nothing for London to launch. A native port returns a real plan here;
    /// a bridge like this one does not.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
        => null;

    // ── connection ────────────────────────────────────────────────────────────

    /// Open a socket and ask what is attached. Returns null when SNI is not
    /// answering at all, an empty array when it answers but has no device --
    /// two states the player has to fix in different places.
    private static async Task<string[]?> ProbeDevicesAsync()
    {
        using var cts = new CancellationTokenSource(ProbeTimeoutMs);
        ClientWebSocket? ws = null;
        try
        {
            ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{DefaultPort}"), cts.Token)
                    .ConfigureAwait(false);
            return await DeviceListAsync(ws, cts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) { return null; }
        catch (OperationCanceledException) { return null; }
        finally
        {
            ws?.Dispose();
        }
    }

    private static async Task<string[]> DeviceListAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await SendJsonAsync(ws, "DeviceList", null, ct).ConfigureAwait(false);
        string reply = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.TryGetProperty("Results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var e in results.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        return list.ToArray();
    }

    public async Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{DefaultPort}"), ct)
                    .ConfigureAwait(false);

            var devices = await DeviceListAsync(ws, ct).ConfigureAwait(false);
            if (devices.Length == 0) { ws.Dispose(); return false; }

            // First device, deliberately. SNI lists one entry per attached
            // emulator or console; picking for the player when there are
            // several would be a guess, and there is nowhere to ask from here.
            _device = devices[0];
            await SendJsonAsync(ws, "Attach", new[] { _device }, ct).ConfigureAwait(false);
            await SendJsonAsync(ws, "Name", new[] { "Multiworld Launcher" }, ct)
                  .ConfigureAwait(false);

            _socket = ws;
            return true;
        }
        catch
        {
            ws.Dispose();
            return false;
        }
    }

    // ── memory ────────────────────────────────────────────────────────────────

    public async Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
    {
        var ws = _socket ?? throw new InvalidOperationException(
            "ReadAsync before a successful ConnectAsync.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendJsonAsync(ws, "GetAddress",
                                new[] { address.ToString("x"), length.ToString("x") }, ct)
                  .ConfigureAwait(false);
            return await ReceiveExactAsync(ws, length, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(long address, byte[] data, CancellationToken ct)
    {
        var ws = _socket ?? throw new InvalidOperationException(
            "WriteAsync before a successful ConnectAsync.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendJsonAsync(ws, "PutAddress",
                                new[] { address.ToString("x"), data.Length.ToString("x") }, ct)
                  .ConfigureAwait(false);
            await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        var ws = _socket;
        _socket = null;
        _device = null;
        if (ws is null) return;

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        .ConfigureAwait(false);
        }
        catch { /* the far end going away first is not an error worth raising */ }
        finally { ws.Dispose(); }
    }

    // ── framing ───────────────────────────────────────────────────────────────

    private static Task SendJsonAsync(ClientWebSocket ws, string opcode,
                                      string[]? operands, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["Opcode"] = opcode, ["Space"] = "SNES" };
        if (operands != null) payload["Operands"] = operands;

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("SNI closed the connection.");
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (r.EndOfMessage) return sb.ToString();
        }
    }

    /// Read exactly `length` bytes. SNI is free to split a reply across frames
    /// and often does for large reads, so "one receive = one answer" would
    /// return a short buffer that looks like real data.
    ///
    /// An EMPTY frame is legal WebSocket fragmentation and must not be treated
    /// as an answer of zero bytes -- but an endless stream of them is a dead
    /// peer, so a bounded number are tolerated and then it is an error.
    private static async Task<byte[]> ReceiveExactAsync(ClientWebSocket ws, int length,
                                                        CancellationToken ct)
    {
        var result = new byte[length];
        int got = 0, emptyFrames = 0;
        while (got < length)
        {
            var r = await ws.ReceiveAsync(new Memory<byte>(result, got, length - got), ct)
                            .ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(
                    $"SNI closed the connection after {got} of {length} bytes.");
            if (r.Count == 0)
            {
                if (++emptyFrames > 64)
                    throw new WebSocketException(
                        $"SNI sent only empty frames after {got} of {length} bytes.");
                continue;
            }
            emptyFrames = 0;
            got += r.Count;
        }
        return result;
    }
}
