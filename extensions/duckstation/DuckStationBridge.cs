using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.DuckStation;

// DuckStation, for the PlayStation 1 worlds that expect it -- as a LAUNCHER.
//
// WHY THERE IS NO MEMORY TRANSPORT HERE
// -------------------------------------
// Spyro 3, the world that put DuckStation on our list, ships a Windows
// desktop program as its client: Uroogla/S3AP is a C#/Avalonia application
// (source/S3AP/S3AP.sln), and its own README says so plainly -- "As the
// mandatory client runs only on Windows, no other systems are supported" --
// listing "the Spyro 3 Archipelago Client and .apworld" as the download.
// That client attaches to DuckStation itself.
//
// So this is client.kind = "world" territory: the world owns the connector,
// and London's job is the disc and the emulator. A transport written here
// would have no caller. If a PS1 world ever arrives that expects LONDON to
// read memory, this file grows one; the header changes with the code.
//
// ⚠ The world's setup guide also carries a step no launcher can do for the
//    player, and the plugin repeats it: in DuckStation, set
//    Settings > Game Properties > Console > Execution Mode to "Interpreter".
//    The recompiler is faster and makes the client read the wrong memory.
//
// ⛔ The player brings their own DuckStation, their own disc image and their
//    own PS1 BIOS. We ship none of them and link to none of them.
public sealed class DuckStationBridge : IEmulatorBridge
{
    public const string Folder = "DuckStation";

    public string   Protocol    => "duckstation";
    public string   DisplayName => "DuckStation";
    public string[] Systems     => new[] { "PSX", "PS1" };
    public string   HomepageUrl => "https://www.duckstation.org/";

    /// True: starting the game on the emulator the world's client expects is
    /// fully implemented here. It does not promise a memory transport, and the
    /// header says why one would have no caller.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            Folder, "DuckStation",
            "https://www.duckstation.org/",
            "duckstation-qt-x64-ReleaseLTCG.exe"),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Resolve(root) is not null) return null;

        return "DuckStation is not in place yet.\n\n"
             + $"Put your own copy in Emulators\\{Folder}\\ so the "
             + "DuckStation executable sits directly in that folder. You can "
             + $"get it from {HomepageUrl}; this launcher never downloads it "
             + "for you.\n\n"
             + "DuckStation also needs a PlayStation BIOS, which has to come "
             + "from your own console -- DuckStation\u0027s own documentation "
             + "says the same, and we do not supply or link to one.\n\n"
             + "PS1 games talk to Archipelago through their own client, which "
             + "you start from the Archipelago Launcher once the game is "
             + "running.";
    }

    /// DuckStation ships under several executable names across its release
    /// channels -- the Qt build has been duckstation-qt-x64-ReleaseLTCG.exe,
    /// duckstation-qt-x64-Release.exe and plain duckstation-qt.exe in living
    /// memory, and the nogui build is different again. Pinning one spelling
    /// would tell a player with a perfectly good install that it is missing,
    /// so look for the declared name first and then for anything that starts
    /// with "duckstation" and ends in .exe.
    private static string? Resolve(string emulatorsRoot)
    {
        string dir = Path.Combine(emulatorsRoot, Folder);
        if (!Directory.Exists(dir)) return null;

        string exact = Path.Combine(dir, "duckstation-qt-x64-ReleaseLTCG.exe");
        if (File.Exists(exact)) return exact;

        foreach (string f in Directory.GetFiles(dir, "duckstation*.exe"))
            return f;
        return null;
    }

    /// Start the player's disc image on their DuckStation.
    ///
    /// The path is passed as a bare argument: DuckStation treats the first
    /// non-option argument as the file to boot. It is NOT placed after "--",
    /// which the PCSX2 work already cost us an afternoon over.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Resolve(emulatorsRoot);
        if (exe is null) return null;

        string args = string.IsNullOrWhiteSpace(context.RomPath)
            ? ""
            : $"\"{context.RomPath}\"";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!);
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "PS1 worlds on DuckStation read its memory through their own "
            + "client; London does not read it.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "PS1 worlds on DuckStation write its memory through their own "
            + "client; London does not write it.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
