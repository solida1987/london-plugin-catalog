using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Xemu;

// xemu, shipped as an installable extension like every other bridge.
//
// WHY THIS IS A LAUNCH BRIDGE AND NOT A MEMORY ONE
// ────────────────────────────────────────────────
// Sneak King is the one game here. Its client reads xemu's process memory directly -- there is no socket to speak -- so all London has to do is start xemu with the disc mounted.
//
// So ReadAsync/WriteAsync stay unsupported on purpose. A bridge that pretended
// to offer them would be advertising a transport nothing here can speak, and
// the first game to ask would fail somewhere far away from the cause.
public sealed class XemuBridge : IEmulatorBridge
{
    public const string Folder = "xemu";
    private const string Exe = "xemu.exe";

    public string   Protocol    => "xemu";
    public string   DisplayName => "xemu";
    public string   HomepageUrl => "https://xemu.app";
    public string[] Systems     => new[] { "XBOX" };

    /// ⚠ FALSE until a game has been played through it.
    ///
    /// The command line below is read out of the world's own client and its
    /// setup guide, but nobody has started xemu from London yet. Claiming
    /// otherwise would put the game in the shop as working on the strength of
    /// code review alone.
    public bool IsReady => false;

    private static readonly LauncherV2.Core.Emulators.EmulatorSource Source =
        new(Author:       "the xemu project",
            Licence:      "GPL-2.0 for xemu and the QEMU it is built on; the distribution also carries firmware files under their own separate terms",
            LicenceUrl:   "https://github.com/xemu-project/xemu/blob/master/LICENSE",
            DownloadPage: "https://xemu.app/docs/download/",
            Owner:        "xemu-project",
            Repo:         "xemu",
            AssetPattern: "xemu-win-x86_64-release.zip");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "xemu", HomepageUrl, Exe, Source),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is null)
            return "xemu is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
                 + $"directly in that folder. You can get it from {HomepageUrl}, or let "
                 + "the launcher fetch that same file from the project's own release once "
                 + "you have seen who wrote it and under what licence.";

        return null;
    }

    /// What the player still has to do in xemu's own settings.
    ///
    /// ⚠ Said, not silently written. We do not know this emulator's config
    /// format well enough to edit it without risking the player's own
    /// settings, and a clear instruction beats a half-correct edit.
    public string ManualSetupNote => "xemu needs your own Xbox BIOS and hard-disk image before it will boot anything.\n\nOpen xemu and go to Settings -> System to point it at them. xemu ships neither, and neither do we.";

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string args = $"-dvd_path \"{context.RomPath}\"";
        if (context.Fullscreen) args += " --fullscreen";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!,
                              new Dictionary<string, string>());
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "xemu is started by London, but the game's own Archipelago world "
            + "carries the code that reads its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "xemu is started by London, but the game's own Archipelago world "
            + "carries the code that writes its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
