using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.TerraTech;

// TerraTechRunner — the whole install, done for the player.
//
// Every other runner we ship asks the player to put a program in
// Emulators\<folder>\. That works for an emulator, which is a small download
// they fetched on purpose. It does not work for TerraTech: the game is
// installed wherever Steam or Epic put it, it is gigabytes, and asking someone
// to copy their game into our folder is not an install flow, it is a chore.
//
// So this runner does the finding itself:
//
//   1. Ask Steam where TerraTech is (registry + libraryfolders.vdf)
//   2. Failing that, look in the usual Epic and Steam locations
//   3. Failing that, use the folder the player pointed at, remembered
//   4. Install our mod into the game's own QMods folder
//   5. Start the game
//
// Steps 1-3 mean that for most people the answer to "where is your game" is
// already known, and the install is one button. Step 4 is the part that used
// to be a paragraph in a setup guide.
public sealed class TerraTechRunner : IEmulatorBridge
{
    public const int SteamAppId = 285920;
    private const string ModFolderName = "TerraTechArchipelago";
    private const string GameExe = "TerraTechWin64.exe";

    public string Protocol => "self";
    public string DisplayName => "TerraTech";
    public string[] Systems => new[] { "PC" };
    public string HomepageUrl => "https://store.steampowered.com/app/285920/TerraTech/";

    /// The mod is ours and the lock is proven in code, but nobody has played a
    /// seed through yet. Saying ready before that is the one claim this whole
    /// project refuses to make.
    public bool IsReady => false;

    /// Nothing for the player to place: we find the game they already own.
    public IReadOnlyList<EmulatorRequirement> Emulators =>
        Array.Empty<EmulatorRequirement>();

    // --- finding the game -------------------------------------------------

    private static string ConfigPath => Path.Combine(
        AppContext.BaseDirectory, "Data", "terratech_path.txt");

    /// Where the player told us the game is, if they ever had to.
    public static string? RememberedPath()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            string p = File.ReadAllText(ConfigPath).Trim();
            return LooksLikeTerraTech(p) ? p : null;
        }
        catch { return null; }
    }

    public static void Remember(string gameDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, gameDir);
        }
        catch { /* a path we cannot cache is a path we look up again */ }
    }

    /// The game's folder, or null when we genuinely cannot find it.
    public static string? FindGameDir()
    {
        string? remembered = RememberedPath();
        if (remembered != null) return remembered;

        // Steam knows exactly where it put things; ask it first.
        try
        {
            string? steam = LauncherV2.Core.SteamLocator.FindGameDir(SteamAppId);
            if (LooksLikeTerraTech(steam)) return steam;
        }
        catch { }

        // Epic and hand-installed copies have no registry entry we can trust,
        // so try the places installers actually use before giving up.
        foreach (string guess in LikelyPlaces())
            if (LooksLikeTerraTech(guess))
                return guess;

        return null;
    }

    private static IEnumerable<string> LikelyPlaces()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string d = drive.RootDirectory.FullName;
            roots.Add(Path.Combine(d, "SteamLibrary", "steamapps", "common", "TerraTech"));
            roots.Add(Path.Combine(d, "Program Files", "Epic Games", "TerraTech"));
            roots.Add(Path.Combine(d, "Epic Games", "TerraTech"));
            roots.Add(Path.Combine(d, "Games", "TerraTech"));
        }
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "TerraTech"));
        return roots;
    }

    /// A folder is TerraTech when the executable is in it. Checking the name
    /// alone would accept an empty folder somebody made by mistake, and the
    /// failure would then land at launch instead of here.
    public static bool LooksLikeTerraTech(string? dir)
        => !string.IsNullOrWhiteSpace(dir)
           && File.Exists(Path.Combine(dir!, GameExe));

    // --- installing the mod -----------------------------------------------

    public static string ModTargetDir(string gameDir)
        => Path.Combine(gameDir, "QMods", ModFolderName);

    /// Is our mod in place, and is it the version that shipped with this
    /// plugin? An out-of-date mod is worse than none: it connects, speaks a
    /// protocol the client no longer uses, and looks like it is working.
    public static bool ModInstalled(string gameDir, string expectedVersion)
    {
        string stamp = Path.Combine(ModTargetDir(gameDir), "installed_version.txt");
        try
        {
            return File.Exists(stamp)
                   && File.ReadAllText(stamp).Trim() == expectedVersion;
        }
        catch { return false; }
    }

    /// Unpack the mod into the game. Returns what happened, in words meant for
    /// the player.
    public static string InstallMod(string gameDir, string modZipPath, string version)
    {
        if (!LooksLikeTerraTech(gameDir))
            return $"That folder does not contain {GameExe}, so it is not TerraTech.";
        if (!File.Exists(modZipPath))
            return "The mod package that ships with this plugin is missing.";

        string target = ModTargetDir(gameDir);
        try
        {
            // Replace rather than merge. A half-old, half-new mod folder is the
            // kind of state that produces a bug nobody can reproduce.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(modZipPath, target);
            File.WriteAllText(Path.Combine(target, "installed_version.txt"), version);
            return $"Mod installed into {target}";
        }
        catch (UnauthorizedAccessException)
        {
            return "Windows refused to write into the game folder. If TerraTech "
                 + "is in Program Files, start London as administrator once, or "
                 + "move the game to a library outside Program Files.";
        }
        catch (IOException e)
        {
            return "Could not write the mod into the game folder: " + e.Message
                 + "  (is TerraTech running?)";
        }
    }

    // --- the bridge contract ---------------------------------------------

    public string? GetUnmetRequirement()
    {
        string? dir = FindGameDir();
        if (dir == null)
            return "TerraTech has not been found on this PC.\n\n"
                 + "London looks for it through Steam and in the usual install "
                 + "folders. If you own it on Epic or installed it somewhere "
                 + "unusual, point London at the folder that contains "
                 + $"{GameExe} and it will remember.";
        return null;
    }

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? dir = FindGameDir();
        if (dir == null) return null;

        // Working directory is the game's own folder: TerraTech loads its data
        // and its mod folder relative to the executable.
        return new LaunchPlan(Path.Combine(dir, GameExe), "", dir);
    }

    // Nothing to connect to. The mod inside the game talks to the Archipelago
    // client directly over TCP 24601 — London starts the game and stands
    // aside. Saying that plainly beats pretending to be a memory bridge.
    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "TerraTech's Archipelago mod talks to its own client; London does "
          + "not read the game's memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "TerraTech's Archipelago mod talks to its own client; London does "
          + "not write to the game's memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
