using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Ppsspp;

// PPSSPP, shipped as an installable extension like every other bridge.
//
// WHY THIS IS A LAUNCH BRIDGE AND NOT A MEMORY ONE
// ────────────────────────────────────────────────
// Monster Hunter Freedom Unite and K-On! After School Live!! both run here, and both worlds talk to PPSSPP's debugger WebSocket themselves (ws://<host>:<port>/debugger, discovered through report.ppsspp.org/match/list). So this bridge only has to start the emulator with the right disc. Both setup guides ask the player to open the ISO by hand; London passes it on the command line instead.
//
// So ReadAsync/WriteAsync stay unsupported on purpose. A bridge that pretended
// to offer them would be advertising a transport nothing here can speak, and
// the first game to ask would fail somewhere far away from the cause.
public sealed class PpssppBridge : IEmulatorBridge
{
    public const string Folder = "PPSSPP";
    private const string Exe = "PPSSPPWindows64.exe";

    public string   Protocol    => "ppsspp";
    public string   DisplayName => "PPSSPP";
    public string   HomepageUrl => "https://www.ppsspp.org";
    public string[] Systems     => new[] { "PSP" };

    /// ⚠ FALSE until a game has been played through it.
    ///
    /// The command line below is read out of the world's own client and its
    /// setup guide, but nobody has started PPSSPP from London yet. Claiming
    /// otherwise would put the game in the shop as working on the strength of
    /// code review alone.
    public bool IsReady => false;

    private static readonly LauncherV2.Core.Emulators.EmulatorSource Source =
        new(Author:       "Henrik Rydgard and the PPSSPP contributors",
            Licence:      "GPL-2.0-or-later for PPSSPP itself; parts of the bundled PSPSDK are BSD-licensed, and its LICENSE.TXT opens with that notice",
            LicenceUrl:   "https://github.com/hrydgard/ppsspp/blob/master/LICENSE.TXT",
            DownloadPage: "https://www.ppsspp.org/download/",
            Owner:        "hrydgard",
            Repo:         "ppsspp",
            AssetPattern: "Windows-x64.zip");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "PPSSPP", HomepageUrl, Exe, Source),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is null)
            return "PPSSPP is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
                 + $"directly in that folder. You can get it from {HomepageUrl}, or let "
                 + "the launcher fetch that same file from the project's own release once "
                 + "you have seen who wrote it and under what licence.";

        return null;
    }

    /// What the player still has to do in PPSSPP's own settings.
    ///
    /// ⚠ Said, not silently written. We do not know this emulator's config
    /// format well enough to edit it without risking the player's own
    /// settings, and a clear instruction beats a half-correct edit.
    public string ManualSetupNote => "PPSSPP's remote debugger is off by default, and both worlds need it.\n\nOpen PPSSPP, go to Tools -> Developer tools and turn on \"Allow remote debugger\". Without it the game runs and the client never sees it.";

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string args = $"\"{context.RomPath}\"";
        if (context.Fullscreen) args += " --fullscreen";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!,
                              new Dictionary<string, string>());
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "PPSSPP is started by London, but the game's own Archipelago world "
            + "carries the code that reads its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "PPSSPP is started by London, but the game's own Archipelago world "
            + "carries the code that writes its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
