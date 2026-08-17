using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Soh;

// Ship of Harkinian, shipped as an installable extension.
//
// SoH is NOT an emulator. It is a native PC port of Ocarina of Time, and its
// Archipelago world (HarbourMasters/Archipelago-SoH, branch oot-soh, game name
// "Ship of Harkinian") imports CommonClient rather than SNIClient or
// BizHawkClient -- the port speaks Archipelago itself.
//
// So there is no bridge to build: London's whole job is to find the program
// where the player put it and start it from there. That is what "native" means
// here, and it is why GetLaunchPlan carries the weight while ReadAsync and
// WriteAsync do nothing.
//
// ⛔ The player installs SoH themselves, and SoH itself needs their own Ocarina
//    of Time ROM to build its assets. We ship neither, and we fetch neither.
public sealed class SohRunner : IEmulatorBridge
{
    public const string Folder = "ShipOfHarkinian";

    public string   Protocol    => "native_soh";
    public string   DisplayName => "Ship of Harkinian";
    public string[] Systems     => new[] { "N64" };
    public string   HomepageUrl => "https://github.com/HarbourMasters/Shipwright/releases";

    /// Ready as soon as the player's copy is in place: nothing has to be
    /// implemented on our side, because the port talks to Archipelago itself.
    /// Unlike the SNI bridge, there is no unfinished transport here.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            Folder, "Ship of Harkinian",
            "https://github.com/HarbourMasters/Shipwright/releases",
            "soh.exe"),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is not null) return null;

        return "Ship of Harkinian is not in place yet.\n\n"
             + $"Put your own copy in Emulators\\{Folder}\\ so that soh.exe sits "
             + "directly in that folder — the note in there says the same. You "
             + $"can get it from {HomepageUrl}; this launcher never downloads "
             + "it for you.\n\n"
             + "SoH also needs your own Ocarina of Time ROM the first time it "
             + "runs, to build its assets. That stays between you and SoH.";
    }

    /// Find it where the player put it, and run it from there.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        // Working directory is the program's own folder: SoH loads oot.otr and
        // its config from beside the executable, so starting it from anywhere
        // else finds nothing.
        return new LaunchPlan(exe, "", Path.GetDirectoryName(exe)!);
    }

    // Nothing to connect to: the port is its own Archipelago client. Saying so
    // plainly beats pretending to be a memory bridge that is never used.
    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "Ship of Harkinian talks to Archipelago itself; London does not read "
            + "its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "Ship of Harkinian talks to Archipelago itself; London does not write "
            + "its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
