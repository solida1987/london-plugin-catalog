using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Cemu;

// Cemu, shipped as an installable extension like every other bridge.
//
// WHY THIS IS A LAUNCH BRIDGE AND NOT A MEMORY ONE
// ────────────────────────────────────────────────
// Xenoblade X is the one game here. Its client installs a Cemu graphic pack and then listens on port 45872 for what that pack posts back, so the emulator side is entirely the world's business -- London only starts Cemu on the right title.
//
// So ReadAsync/WriteAsync stay unsupported on purpose. A bridge that pretended
// to offer them would be advertising a transport nothing here can speak, and
// the first game to ask would fail somewhere far away from the cause.
public sealed class CemuBridge : IEmulatorBridge
{
    public const string Folder = "Cemu";
    private const string Exe = "Cemu.exe";

    public string   Protocol    => "cemu";
    public string   DisplayName => "Cemu";
    public string   HomepageUrl => "https://cemu.info";
    public string[] Systems     => new[] { "WIIU" };

    /// ⚠ FALSE until a game has been played through it.
    ///
    /// The command line below is read out of the world's own client and its
    /// setup guide, but nobody has started Cemu from London yet. Claiming
    /// otherwise would put the game in the shop as working on the strength of
    /// code review alone.
    public bool IsReady => false;

    private static readonly LauncherV2.Core.Emulators.EmulatorSource Source =
        new(Author:       "the Cemu project",
            Licence:      "MPL-2.0",
            LicenceUrl:   "https://github.com/cemu-project/Cemu/blob/main/LICENSE.txt",
            DownloadPage: "https://github.com/cemu-project/Cemu/releases",
            Owner:        "cemu-project",
            Repo:         "Cemu",
            AssetPattern: "windows-x64.zip");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "Cemu", HomepageUrl, Exe, Source),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is null)
            return "Cemu is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
                 + $"directly in that folder. You can get it from {HomepageUrl}, or let "
                 + "the launcher fetch that same file from the project's own release once "
                 + "you have seen who wrote it and under what licence.";

        return null;
    }

    /// What the player still has to do in Cemu's own settings.
    ///
    /// ⚠ Said, not silently written. We do not know this emulator's config
    /// format well enough to edit it without risking the player's own
    /// settings, and a clear instruction beats a half-correct edit.
    public string ManualSetupNote => "Cemu needs its community graphic packs downloaded before Xenoblade X's world can add its own.\n\nStart Cemu once and let it fetch them from Options -> Graphic packs, then press Play again.";

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string args = $"-g \"{context.RomPath}\"";
        if (context.Fullscreen) args += " --fullscreen";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!,
                              new Dictionary<string, string>());
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "Cemu is started by London, but the game's own Archipelago world "
            + "carries the code that reads its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "Cemu is started by London, but the game's own Archipelago world "
            + "carries the code that writes its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
