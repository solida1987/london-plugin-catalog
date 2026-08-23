using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Dolphin;

// Dolphin, for the GameCube and Wii worlds -- as a LAUNCHER, not a memory
// bridge, and that is a deliberate and checkable choice.
//
// WHY THERE IS NO MEMORY TRANSPORT HERE
// -------------------------------------
// Every GameCube/Wii world in the catalogue reads Dolphin's memory ITSELF,
// through the dolphin-memory-engine Python package, and every one of them
// registers its own Archipelago client:
//
//   Metroid Prime   UltiNaruto/MetroidAPrime   src/DolphinClient.py,
//                                              src/__init__.py -> Type.CLIENT
//   Mario Kart: DD  aXu-AP/archipelago-double-dash
//                                              game_state.py imports
//                                              dolphin_memory_engine;
//                                              __init__.py registers
//                                              "Mario Kart Double Dash Client"
//   Skyward Sword   Battlecats59/SS_APWorld    SSClient.py, Type.CLIENT
//   TTYD            jamesbrq/ArchipelagoTTYD   TTYDClient.py, ttyd_runtime.py,
//                                              Type.CLIENT
//
// So every one of them is client.kind = "world": the world owns the connector
// end to end. A second reader attached to the same Dolphin process would not
// add anything -- it would be a competing pair of hands on the same memory.
//
// Writing a full dolphin-memory-engine port in C# would therefore be work with
// no consumer. If a GameCube world ever arrives that expects LONDON to read
// memory, this file grows a transport and IsReady goes back to describing it;
// until then the honest shape of this extension is "find Dolphin, start the
// disc, stand aside", exactly like SohRunner.
//
// ⛔ The player brings their own Dolphin and their own disc images. We ship
//    neither, fetch neither, and never link to a place that hosts them.
public sealed class DolphinBridge : IEmulatorBridge
{
    public const string Folder = "Dolphin";

    public string   Protocol    => "dolphin";
    public string   DisplayName => "Dolphin";
    public string[] Systems     => new[] { "GC", "Wii", "WII" };
    public string   HomepageUrl => "https://dolphin-emu.org/download/";

    /// True: what this extension promises -- starting the game on the emulator
    /// the world's client expects -- is fully implemented. It does NOT promise
    /// a memory transport, and the comment above says why one would have no
    /// caller. Compare SniBridge, which was listed while its transport was
    /// unwritten; the difference is that nothing here is missing.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            Folder, "Dolphin",
            "https://dolphin-emu.org/download/",
            "Dolphin.exe"),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is not null) return null;

        return "Dolphin is not in place yet.\n\n"
             + $"Put your own copy in Emulators\\{Folder}\\ so that Dolphin.exe "
             + "sits directly in that folder. You can get it from "
             + $"{HomepageUrl}; this launcher never downloads it for you.\n\n"
             + "GameCube and Wii games talk to Archipelago through their own "
             + "client, which you start from the Archipelago Launcher once the "
             + "game is running. This launcher's part is the disc and Dolphin.";
    }

    /// Start the player's disc image on their Dolphin.
    ///
    /// -b exits Dolphin when the game is closed rather than dropping back to
    /// the game list, which is what a player who pressed Play in London
    /// expects. -e takes the path; quoting matters because disc images live in
    /// folders with spaces far more often than not.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string args = string.IsNullOrWhiteSpace(context.RomPath)
            ? ""
            : $"-b -e \"{context.RomPath}\"";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!);
    }

    // No transport, and saying so is the point -- see the header. A stub that
    // returned zeroes would let a future world "connect" and read nothing.
    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "GameCube and Wii worlds read Dolphin's memory through their own "
            + "client (dolphin-memory-engine); London does not read it.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "GameCube and Wii worlds write Dolphin's memory through their own "
            + "client (dolphin-memory-engine); London does not write it.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
