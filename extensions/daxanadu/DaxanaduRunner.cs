using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Daxanadu;

// Daxanadu, shipped as an installable runner.
//
// Daxanadu is not a general emulator and not a port. Its own README is exact
// about what it is: "Daxanadu is not a clone, a remake nor a source port.
// Daxanadu is an NES emulator that only works with a Faxanadu rom file."
// Archipelago support is built into the program -- the player opens its
// ARCHIPELAGO menu and types the server address, slot name and password
// there. There is no external client and no connector script.
//
// So there is nothing to bridge. London finds the program where the player
// put it and starts it, exactly as SohRunner does for Ship of Harkinian, and
// the launch note tells the player the one thing they must do next.
//
// ⛔ WE SHIP NOTHING. Not Daxanadu -- MIT-licensed and free, but still the
//    player\u0027s to fetch -- and above all not the ROM. The author is blunt
//    about that too: "You must own a legal copy of that rom. I will not
//    provide it for you, so don\u0027t ask." Neither will we.
public sealed class DaxanaduRunner : IEmulatorBridge
{
    public const string Folder = "Daxanadu";

    /// The ROM name Daxanadu insists on. From its README: "Rename it to
    /// exactly: `Faxanadu (U).nes`." Not a guess and not ours to relax --
    /// the program looks for that filename beside itself.
    public const string RomName = "Faxanadu (U).nes";

    public string   Protocol    => "daxanadu";
    public string   DisplayName => "Daxanadu";
    public string[] Systems     => new[] { "NES" };
    public string   HomepageUrl => "https://github.com/Daivuk/Daxanadu/releases";

    /// True: everything this extension promises is implemented. It promises a
    /// launch, not a transport, because the program talks to Archipelago
    /// itself.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            Folder, "Daxanadu",
            "https://github.com/Daivuk/Daxanadu/releases",
            "Daxanadu.exe"),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        string? exe = Emulators[0].Resolve(root);

        if (exe is null)
            return "Daxanadu is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that "
                 + "Daxanadu.exe sits directly in that folder. It is free and "
                 + $"MIT-licensed: {HomepageUrl}. This launcher never "
                 + "downloads it for you.";

        // The ROM check is part of readiness here, not an afterthought: the
        // program will start without it and then sit on an error, which reads
        // like our failure rather than a missing file.
        string rom = Path.Combine(Path.GetDirectoryName(exe)!, RomName);
        if (!File.Exists(rom))
            return "Daxanadu is in place, but its ROM is not.\n\n"
                 + $"Daxanadu needs your own Faxanadu ROM, named exactly "
                 + $"\u0022{RomName}\u0022, in the same folder as "
                 + "Daxanadu.exe. The English (U) version is the one it "
                 + "supports.\n\n"
                 + "We do not supply it, and neither does Daxanadu\u0027s "
                 + "author -- their words: you must own a legal copy.";

        return null;
    }

    /// Find it where the player put it, and run it from there. The working
    /// directory is the program\u0027s own folder: Daxanadu loads the ROM, its
    /// assets and FCEUX.pal from beside the executable.
    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;
        return new LaunchPlan(exe, "", Path.GetDirectoryName(exe)!);
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "Daxanadu talks to Archipelago itself, from its own ARCHIPELAGO "
            + "menu; London does not read its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "Daxanadu talks to Archipelago itself, from its own ARCHIPELAGO "
            + "menu; London does not write its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
