using System;
using System.Net.Sockets;
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
// ⛔ This extension carries PROTOCOL ONLY. The player installs SNI and their
//    emulator themselves; we never fetch either. Tools/lint_no_emulator_download.py
//    covers Core/Extensions for exactly this reason.
//
// STATE: the transport is NOT finished. Reachability is real -- we can tell you
// whether SNI is running -- but memory reads and writes are not implemented, so
// IsReady stays false and the launcher refuses to start a game with it rather
// than letting somebody play into silence. Everything below is honest about
// which half works.
public sealed class SniBridge : IEmulatorBridge
{
    // SNI's default gRPC port. Published by the SNI project; the player can
    // move it, which is why a failed check says "not reachable" rather than
    // "not installed".
    private const int DefaultPort = 8191;
    private const int ProbeTimeoutMs = 400;

    public string   Protocol    => "sni";
    public string   DisplayName => "SNI (Super Nintendo Interface)";
    public string[] Systems     => new[] { "SNES" };
    public string   HomepageUrl => "https://github.com/alttpo/sni";

    /// ⛔ Stays false until reads and writes are proven against a real game.
    /// The same honesty gate as EmulatorBackend.BridgeReady: an unfinished
    /// bridge is explained, never silently offered.
    public bool IsReady => false;

    public string? GetUnmetRequirement()
    {
        if (!IsSniReachable())
            return "SNI does not appear to be running.\n\n"
                 + "Start SNI and connect it to your emulator, then try again. "
                 + $"You can get SNI from {HomepageUrl} — this launcher never "
                 + "downloads it for you.";

        return "SNI is running, but this bridge cannot carry checks yet: the "
             + "memory transport is not implemented.\n\n"
             + "The game would run and never send a single check, so it is "
             + "refused rather than started.";
    }

    /// A plain TCP connect. Enough to tell "SNI is up" from "SNI is not up",
    /// and deliberately nothing more -- claiming to speak a protocol we have
    /// not implemented would be the failure this whole design exists to stop.
    private static bool IsSniReachable()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", DefaultPort);
            return connect.Wait(ProbeTimeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(false);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotImplementedException(
            "The SNI memory transport is not implemented. IsReady is false, so "
            + "the launcher should never have reached this call.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotImplementedException(
            "The SNI memory transport is not implemented. IsReady is false, so "
            + "the launcher should never have reached this call.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
