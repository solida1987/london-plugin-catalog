using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.BizHawk;

// BizHawk, shipped as an installable extension like every other bridge.
//
// This is the one transport that was proven end to end (Pokemon Emerald), and
// it used to be compiled into the launcher. Moving it out here is the point:
// London now has NO emulator knowledge of its own -- every emulator arrives as
// an extension, and this one just happens to ship pre-installed so a fresh copy
// works out of the box.
//
// ⛔ The player installs BizHawk themselves into Emulators\BizHawk\. We never
//    fetch it.
//
// The memory transport is NOT here: BizHawk's AP bridge is two named pipes that
// the in-emulator Lua connector opens, and the launcher owns those pipes for the
// whole session. So ReadAsync/WriteAsync stay unsupported and this bridge's job
// is exactly what its name says -- say where BizHawk is and how to start it.
public sealed class BizHawkBridge : IEmulatorBridge
{
    public const string Folder = "BizHawk";
    private const string Exe = "EmuHawk.exe";

    public string   Protocol    => "bizhawk";
    public string   DisplayName => "BizHawk";
    public string   HomepageUrl => "https://tasvideos.org/BizHawk/ReleaseHistory";

    /// The systems BizHawk can host. Matched against a plugin's RomSystem.
    public string[] Systems => new[]
        { "GBA", "GBC", "GB", "SNES", "NES", "N64", "GEN", "SMS", "PCE" };

    /// Proven: this is the transport the GBA plugins already run on.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "BizHawk", HomepageUrl, Exe),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is not null) return null;

        return "BizHawk is not in place yet.\n\n"
             + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
             + "directly in that folder — the note in there says the same. You "
             + $"can get it from {HomepageUrl}; this launcher never downloads it "
             + "for you.";
    }

    /// The exact command line the launcher used before this was an extension.
    ///
    /// ⚠ No --system flag: BizHawk has no such argument (its ArgParser rejects
    ///   it with "Unrecognized command or argument"). The core is detected from
    ///   the ROM file itself.
    ///
    /// ⚠ WorkingDirectory is the BizHawk folder so the connector's relative
    ///   "ap_config.json" fallback resolves; AP_CONFIG_PATH carries the absolute
    ///   path, which the Lua reads with os.getenv because a path with spaces
    ///   does not survive as a command-line argument.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string dir = Path.GetDirectoryName(exe)!;
        string args = $"\"{context.RomPath}\" --lua=\"{context.ScriptPath}\""
                    + (context.Fullscreen ? " --fullscreen" : "");

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(context.ConfigPath))
            env["AP_CONFIG_PATH"] = context.ConfigPath;

        return new LaunchPlan(exe, args, dir, env);
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    // The launcher owns the two named pipes for the whole session; this bridge
    // never reads memory itself.
    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "BizHawk's AP transport is the launcher's own named-pipe bridge, "
            + "driven by the in-emulator Lua connector.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "BizHawk's AP transport is the launcher's own named-pipe bridge, "
            + "driven by the in-emulator Lua connector.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
