using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using LauncherV2.Core;
using LauncherV2.Core.Plugins;

namespace LauncherV2.Plugins.Catalog;

// One plugin, many PC games — the sibling of GenericEmulatorPlugin, and the
// same idea: behaviour comes from an embedded pc.json, so a new game is a data
// file rather than a new class.
//
// WHAT A PC GAME NEEDS THAT AN EMULATED ONE DOES NOT
// An emulated game is played THROUGH London: the launcher owns the emulator,
// reads its memory and reports the checks. A PC game is not. Its Archipelago
// support is a mod inside the game, and that mod talks to the server itself.
// London cannot sit in the middle of that conversation, and pretending to
// would be a lie with a progress bar on it.
//
// So this plugin does the four things London genuinely can:
//
//   1. INSTALL THE WORLD. The .apworld goes into the engine's custom_worlds,
//      which is what makes the game appear in New seed at all.
//   2. INSTALL THE MOD — really install it — when the author ships a plain
//      .zip whose layout London recognises (a BepInEx/MelonLoader tree, a
//      plugins/ tree, bare plugin dlls) and the player has pointed at their
//      own game folder. When the layout is NOT recognisable, nothing is
//      guessed: the file is downloaded, the player is told exactly that, and
//      the setup check hands them the folder and the file so they can do the
//      one step London cannot.
//   3. CHECK THE SETUP. DetectComponents answers "what is present, what is
//      missing" per piece — world, game folder, mod — in the launcher's
//      green/amber/red house style, and ShowComponentSetup is the guided
//      walkthrough that fixes each one.
//   4. START THE GAME and hand over the address and slot name to type into
//      the game's own connect screen.
//
// Every download comes from the author's own release, and only from addresses
// the plugin.json `declares` block already named — the consent screen must
// list every URL this code can fetch. Nothing is bundled.
public class GenericPcPlugin : IGamePlugin
{
    protected readonly PcManifest Manifest;

    public GenericPcPlugin()
    {
        var asm = GetType().Assembly;
        string? name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("pc.json", StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException(
                $"{asm.GetName().Name} has no embedded pc.json.");

        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        Manifest = PcManifest.Parse(r.ReadToEnd());
    }

    /// For proofs. The public constructor reads the embedded pc.json, which
    /// means one assembly can only ever BE one game — but the negative tests
    /// ("does a .7z game refuse to claim AutoMod?") need one harness to wear
    /// every install kind in turn. Injection is for test harnesses only; a
    /// shipped plugin always carries its manifest inside itself.
    protected GenericPcPlugin(PcManifest manifest) => Manifest = manifest;

    // ------------------------------------------------------------- identity

    public string GameId      => Manifest.Id;
    public string DisplayName => Manifest.DisplayName;
    public string Subtitle    => Manifest.Subtitle;
    public string ApWorldName => Manifest.ApWorldName;
    public string Description => Manifest.Description;

    public string GameDirectory => Path.Combine(
        AppContext.BaseDirectory, "Games", "PC", GameId);

    public string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", $"{GameId}.png");

    public string ThemeAccentColor => "#5B92D4";
    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    /// True when the author's mod is a plain zip London can open in-process
    /// and try to place. A .7z can be FETCHED but not OPENED (the BCL has no
    /// 7z reader), so for those the honest offer is download-and-hand-over.
    private bool ModIsAppliableArchive => PcSetup.IsZipUrl(Manifest.ModUrl);

    /// True when the mod is a file at an address London may download at all —
    /// the same rule the catalogue packer uses when writing `declares`, so
    /// this code can never fetch an address the consent screen did not name.
    private bool ModIsDirectFile => PcSetup.IsDirectFileUrl(Manifest.ModUrl);

    public string[] GameBadges => Manifest.Install switch
    {
        "apworld_only"    => new[] { "PC", "Adds itself to your seeds" },
        // The badge states which half is automatic, per game, because the
        // answer differs: a .zip mod is installed by London, a .7z is
        // downloaded and handed over.
        "apworld_and_mod" => ModIsAppliableArchive
                                 ? new[] { "PC", "World + mod auto" }
                                 : new[] { "PC", "World auto · mod by hand" },
        "apworld_and_external_mod" => new[] { "PC", "World auto · mod by hand" },
        "mod_package"     => ModIsAppliableArchive
                                 ? new[] { "PC", "Game mod auto" }
                                 : new[] { "PC", "Mod by hand" },
        "bundled"         => new[] { "PC", "Ships with Archipelago" },
        _                 => new[] { "PC", "Manual setup" },
    };

    /// What London can honestly promise. The button label is built from this,
    /// so the mapping IS the promise:
    ///
    ///   AutoInstall — click Install, everything happens.
    ///   AutoMod     — you point at your game, London installs the rest.
    ///   ManualSetup — London installs what it can (usually the world) and
    ///                 walks you through the rest; part of the job is yours.
    ///
    /// ⚠ AutoMod is claimed ONLY when the mod is a .zip London can actually
    /// open and place. This plugin used to claim AutoMod for every
    /// "apworld_and_mod" game and then install only the world — the player
    /// pressed a button labelled "Install mod" and no mod was installed. The
    /// same rule keeps .7z mods (fetchable, not openable) and page-link mods
    /// (Thunderstore, Steam Workshop) at ManualSetup: under-claiming costs a
    /// click, over-claiming costs the player a silent game that never sends
    /// a check.
    public InstallCapability InstallCapability => Manifest.Install switch
    {
        "apworld_only" => InstallCapability.AutoInstall,
        "bundled"      => InstallCapability.AutoInstall,
        "apworld_and_mod" or "mod_package" when ModIsAppliableArchive
                       => InstallCapability.AutoMod,
        _              => InstallCapability.ManualSetup,
    };

    /// Credit where it is owed, in the player's face rather than a footnote.
    ///
    /// ⚠ A PROPERTY, not a method. Written as Credits() first, which compiled
    /// perfectly and satisfied nothing: the interface's default property kept
    /// answering "no credits", so every one of these games would have shipped
    /// naming nobody. Caught by a proof that read the loaded plugin instead of
    /// the source.
    public IReadOnlyList<GameCredit> Credits
    {
        get
        {
            var list = new List<GameCredit>
            {
                new("The game", "belongs to its publisher -- you supply your own copy",
                    Highlight: true),
            };
            if (!string.IsNullOrWhiteSpace(Manifest.WorldBy))
                list.Add(new GameCredit("Archipelago world by", Manifest.WorldBy!,
                                        Highlight: true));
            if (!string.IsNullOrWhiteSpace(Manifest.Licence)
                && Manifest.Licence != "NOASSERTION")
                list.Add(new GameCredit("World licence", Manifest.Licence!));
            list.Add(new GameCredit("Launcher plugin by", "solida1987"));
            return list;
        }
    }

    // ------------------------------------------------------------- install

    /// Where the engine keeps the worlds a seed can be generated from. Written
    /// only through ApworldSync's target when the player set one; otherwise
    /// the engine London itself discovered.
    private static string? CustomWorldsDir()
    {
        var settings = SettingsStore.Load();
        var engine = LauncherV2.Core.Archipelago.ApEngine.Discover(
            string.IsNullOrWhiteSpace(settings.ApEnginePath) ? null : settings.ApEnginePath);
        return engine is { Usable: true } ? engine.CustomWorldsDir : null;
    }

    private string ApworldPath
    {
        get
        {
            string? dir = CustomWorldsDir();
            return dir == null ? "" : Path.Combine(dir, Manifest.ApworldFileName);
        }
    }

    /// The player's own install of the game, if they have pointed London at
    /// it. The launcher's locate flow writes this key; the setup check writes
    /// the same one, so both doors lead to the same room.
    private string? RegisteredGameFolder
    {
        get
        {
            var s = SettingsStore.Load();
            if (s.OriginalGameLocations.TryGetValue(GameId, out var f)
                && !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
                return f;

            // The player never told us -- but Steam probably knows. Asking
            // costs a millisecond and saves them a hunt through Program Files.
            string? found = AutoFindGameFolder();
            if (found != null)
            {
                // Remember it, so the answer is stable and the player can see
                // and change it in Settings like any other game location.
                try
                {
                    s.OriginalGameLocations[GameId] = found;
                    SettingsStore.Save(s);
                }
                catch { /* a folder we cannot cache is one we look up again */ }
            }
            return found;
        }
    }

    /// Where this game is installed, according to the machine rather than the
    /// player. Null when we genuinely cannot tell -- never a guess.
    ///
    /// ⚠ Every candidate is confirmed to contain an executable before it is
    /// returned. A path that merely LOOKS right would turn a question the
    /// player can answer into an install that quietly does nothing.
    private string? AutoFindGameFolder()
    {
        try
        {
            if (int.TryParse(Manifest.SteamAppId, out int appId) && appId > 0)
            {
                string? dir = LauncherV2.Core.SteamLocator.FindGameDir(appId);
                if (LooksLikeAGame(dir)) return dir;
            }
        }
        catch { /* Steam not installed, or a registry we may not read */ }

        // Epic and hand-installed copies leave nothing to query, so try where
        // installers actually put things. The folder is named after the game.
        string leaf = SafeFolderLeaf(Manifest.DisplayName);
        if (leaf.Length == 0) return null;

        foreach (string root in LikelyGameRoots())
        {
            try
            {
                string candidate = Path.Combine(root, leaf);
                if (LooksLikeAGame(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> LikelyGameRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string d = drive.RootDirectory.FullName;
            yield return Path.Combine(d, "SteamLibrary", "steamapps", "common");
            yield return Path.Combine(d, "Program Files", "Epic Games");
            yield return Path.Combine(d, "Epic Games");
            yield return Path.Combine(d, "Games");
        }
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common");
    }

    private static string SafeFolderLeaf(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name ?? "")
            if (!Path.GetInvalidFileNameChars().Contains(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    /// A folder is a game when it holds an executable. Checking the name alone
    /// would accept an empty folder somebody made by mistake.
    private static bool LooksLikeAGame(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try { return Directory.EnumerateFiles(dir, "*.exe").Any(); }
        catch { return false; }
    }

    /// Where a downloaded mod archive lands, receipt or not. Kept under the
    /// game's own launcher folder so "open the downloaded file" always has a
    /// file to open, and so uninstalling the plugin folder removes it too.
    private string DownloadedModPath => Manifest.ModUrl is { Length: > 0 } u
        ? Path.Combine(GameDirectory, "downloads", u.Rsplit())
        : "";

    private string ModReceiptPath => Path.Combine(GameDirectory, "mod_receipt.json");

    public bool IsInstalled => Manifest.Install switch
    {
        // The world ships inside Archipelago; nothing of ours is installed,
        // and saying "not installed" about a game the engine already knows
        // would send the player looking for a download that does not exist.
        "bundled" => CustomWorldsDir() != null,
        "manual" or "discord_only" => Directory.Exists(GameDirectory),
        // A pure mod package has no world file to point at: installed means
        // the mod is in the player's game folder — our receipt, or the scan
        // that recognises a hand-made install of the same files.
        "mod_package" => PcSetup.ReadReceipt(ModReceiptPath) != null
                         || PcSetup.HandInstallLooksPresent(
                                RegisteredGameFolder, DownloadedModPath),
        // ⚠ apworld_and_external_mod belongs HERE, in the default, not up with
        // "manual". Its capability is ManualSetup, which makes it tempting to
        // group the two -- but London really does install a world file for it,
        // and the folder test would report "not installed" for a game whose
        // world is sitting in custom_worlds. What is installed is the world.
        _ => ApworldPath.Length > 0 && File.Exists(ApworldPath),
    };

    public string? InstalledVersion
        => IsInstalled ? (Manifest.Version.Length > 0 ? Manifest.Version : "installed") : null;

    public string? AvailableVersion => Manifest.Version.Length > 0 ? Manifest.Version : null;

    public Task CheckForUpdateAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// True while THIS plugin instance is inside an install — the setup
    /// check's "Install" button and the launcher's install flow could
    /// otherwise run concurrently over the same .part files.
    private int _installing;

    public async Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                                           CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _installing, 1) == 1)
            throw new InvalidOperationException(
                "An install for this game is already running.");
        try
        {
            await InstallCoreAsync(progress, ct).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _installing, 0); }
    }

    private async Task InstallCoreAsync(IProgress<(int Pct, string Msg)> progress,
                                        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);

        if (Manifest.Install == "bundled")
        {
            progress?.Report((100, $"{DisplayName} ships with Archipelago itself — "
                                 + "nothing to download."));
            return;
        }

        bool hasWorld = !string.IsNullOrWhiteSpace(Manifest.ApworldUrl);
        bool triesMod = Manifest.Install is "apworld_and_mod" or "mod_package"
                        && ModIsDirectFile;

        // A game with nothing London can fetch still deserves a place in the
        // library: the guide and the links ARE the install. This used to
        // throw, which turned "the setup is by hand" into an error dialog.
        if (!hasWorld && !triesMod)
        {
            progress?.Report((100,
                $"{DisplayName} added to your library. Its setup is done by hand — "
              + "the author's steps and links are on the game's page."));
            return;
        }

        if (hasWorld)
            await InstallWorldAsync(progress, endPct: triesMod ? 50 : 100, ct)
                .ConfigureAwait(false);

        if (!triesMod)
        {
            // The world is in; say plainly what is NOT included so the last
            // line of the install never oversells it.
            if (Manifest.Install == "apworld_and_external_mod")
                progress?.Report((100, $"World installed. {DisplayName}'s in-game mod "
                    + "comes from Thunderstore or the Steam Workshop — the game's "
                    + "page has the author's steps."));
            else if (Manifest.ModUrl != null)
                progress?.Report((100, $"World installed. {DisplayName} also needs its "
                    + "in-game mod — open the setup check on the game's page."));
            else
                progress?.Report((100, $"{DisplayName} can now be picked in New seed."));
            return;
        }

        await InstallModAsync(progress, startPct: hasWorld ? 55 : 5,
                              worldInstalled: hasWorld, ct).ConfigureAwait(false);
    }

    private async Task InstallWorldAsync(IProgress<(int Pct, string Msg)>? progress,
                                         int endPct, CancellationToken ct)
    {
        string? worlds = CustomWorldsDir();
        if (worlds == null)
            throw new InvalidOperationException(
                "No Archipelago engine is set up yet. Open Multiworld and point London "
              + "at one (or let it fetch the installer) — the world file has to go into "
              + "the engine's own folder to be usable.");

        progress?.Report((5, $"Downloading {Manifest.ApworldFileName} from "
                           + $"{Manifest.WorldBy}'s own release…"));

        byte[] data = await PcSetup.DownloadAsync(Manifest.ApworldUrl!, ct)
                                   .ConfigureAwait(false);

        // An .apworld is a zip. A release asset that moved serves an error
        // page, and an error page in custom_worlds breaks EVERY generation --
        // not just this game's.
        if (!PcSetup.LooksLikeZip(data))
            throw new InvalidDataException(
                $"What came back from {Manifest.ReleasePage} was not a world file. "
              + "The release may have moved — install it by hand from there.");

        Directory.CreateDirectory(worlds);
        string dest = Path.Combine(worlds, Manifest.ApworldFileName);
        string tmp = dest + ".part";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, dest, overwrite: true);

        progress?.Report((endPct, "World installed."));
    }

    /// The mod half. Downloads always (the file is the hand-over when the
    /// layout defeats us); applies only what PcSetup.PlanZip is SURE about.
    private async Task InstallModAsync(IProgress<(int Pct, string Msg)>? progress,
                                       int startPct, bool worldInstalled,
                                       CancellationToken ct)
    {
        string doneLead = worldInstalled ? "World installed. " : "";

        string? gameFolder = RegisteredGameFolder;
        if (gameFolder == null)
        {
            // Not an error: the world half (if any) succeeded, and the setup
            // check is the door to fixing this. Throwing here would report a
            // finished world install as a failure.
            progress?.Report((100, doneLead
                + $"To add the mod, point London at your {DisplayName} folder "
                + "(setup check on the game's page) and run Install again."));
            return;
        }

        progress?.Report((startPct, $"Downloading the {DisplayName} mod from "
                                  + "the author's own release…"));
        byte[] data = await PcSetup.DownloadAsync(Manifest.ModUrl!, ct).ConfigureAwait(false);

        string archive = DownloadedModPath;
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        // A moved release asset serves an HTML error page. Handing THAT over as
        // "the mod" would waste the player's evening either way, so both
        // archive types are checked against their magic bytes.
        bool isZipName = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        bool looksReal = isZipName
            ? PcSetup.LooksLikeZip(data)
            : !archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
              || (data.Length >= 2 && data[0] == 0x37 && data[1] == 0x7A);
        if (!looksReal)
            throw new InvalidDataException(
                $"What came back from {Manifest.ReleasePage} was not the mod archive. "
              + "The release may have moved — get it by hand from there.");

        string tmp = archive + ".part";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, archive, overwrite: true);

        if (!isZipName)
        {
            // .7z (or a bare file we cannot open). Downloaded, not guessed at.
            progress?.Report((100, doneLead
                + $"The mod is downloaded to {archive}, but London cannot open this "
                + "archive type to place its files. Open the setup check — it hands "
                + "you the file and your game folder for the one step left."));
            return;
        }

        progress?.Report((80, "Checking the mod archive's layout…"));

        var receiptFiles = PcSetup.ReadReceipt(ModReceiptPath)?.Files
                               .Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                           ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        PcSetup.ZipPlan plan;
        using (var za = ZipFile.OpenRead(archive))
            plan = PcSetup.PlanZip(za.Entries.Select(e => (e.FullName, e.Length)).ToList(),
                                   gameFolder, receiptFiles);

        if (plan.Reason != null)
        {
            // The layout is not one London recognises. DO NOT GUESS — a file
            // in the wrong folder is silently ignored by the game, and the
            // player would spend an evening wondering why no checks arrive.
            progress?.Report((100, doneLead
                + $"The mod is downloaded, but its layout is not one London "
                + $"recognises ({plan.Reason}), so nothing was placed by guesswork. "
                + "Open the setup check — it hands you the file and your game "
                + "folder for the one step left."));
            return;
        }

        progress?.Report((85, $"Installing the mod into {gameFolder}…"));

        var installed = PcSetup.ApplyPlan(archive, gameFolder, plan, ct);

        PcSetup.WriteReceipt(ModReceiptPath, new PcSetup.Receipt(
            Manifest.ModUrl!, Manifest.Version, installed));

        // Verify what was just claimed, before claiming it.
        var missing = installed.Where(f => !File.Exists(Path.Combine(gameFolder, f.Path)))
                               .ToList();
        if (missing.Count > 0)
            throw new IOException(
                $"{missing.Count} of {installed.Count} mod files are not on disk after "
              + $"install (first: {missing[0].Path}). An antivirus may be removing "
              + "them — whitelist the game folder and run Install again.");

        string skipped = plan.SkippedExisting.Count == 0 ? "" :
            $" ({plan.SkippedExisting.Count} existing config file(s) were left untouched.)";
        progress?.Report((100, doneLead
            + $"Mod installed — {installed.Count} file(s) placed in your game folder "
            + $"and verified.{skipped}"));
    }

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    /// The folder the player claims is the game. London cannot know every
    /// game's file list, so this rejects only what is PROVABLY wrong — no
    /// executable anywhere near the top, or one of our own folders picked by
    /// misclick. A name mismatch does not reject: authors' exe names diverge
    /// from their titles far too often ("Ori..." ships as oriDE.exe), and a
    /// hard block with no override strands exactly the players who are right.
    /// The setup check states what evidence was found instead.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";

        string full = Path.GetFullPath(folder);
        if (full.StartsWith(Path.GetFullPath(AppContext.BaseDirectory),
                            StringComparison.OrdinalIgnoreCase))
            return "That is the launcher's own folder — pick the folder where "
                 + $"{DisplayName} itself is installed.";

        if (File.Exists(Path.Combine(full, "ArchipelagoLauncher.exe")))
            return "That looks like the Archipelago engine's folder, not "
                 + $"{DisplayName}'s.";

        if (!PcSetup.HasExecutable(full))
            return $"No program (.exe) was found there. Pick the folder that holds "
                 + $"{DisplayName}'s own executable — for Steam games that is "
                 + "usually steamapps\\common\\<game>.";

        return null;
    }

    // ---------------------------------------------------- the setup check

    // The guided checker, in the launcher's existing house pattern (the one
    // Diablo II uses): DetectComponents states what is present and what is
    // missing, the launcher paints them green/amber/red and refuses launch
    // while anything Required is absent, and ShowComponentSetup is the dialog
    // that walks the player through fixing each item.

    public IReadOnlyList<GameComponent> DetectComponents()
    {
        var list = new List<GameComponent>();
        string kind = Manifest.Install;

        // The world file, for every kind that has one.
        if (!string.IsNullOrWhiteSpace(Manifest.ApworldUrl))
        {
            bool worldOk = ApworldPath.Length > 0 && File.Exists(ApworldPath);
            list.Add(new GameComponent(
                "Archipelago world", worldOk, ComponentNeed.Required,
                worldOk ? $"{Manifest.ApworldFileName} is in the engine's custom_worlds."
                        : "The world file is not in the engine yet.",
                worldOk ? null : "Click Install — London downloads it from the "
                               + "author's release."));
        }

        if (kind is "apworld_and_mod" or "mod_package")
        {
            string? folder = RegisteredGameFolder;
            bool located = folder != null;
            string? evidence = located
                ? PcSetup.NameEvidence(folder!, DisplayName) : null;
            list.Add(new GameComponent(
                "Your copy of the game", located, ComponentNeed.Required,
                located
                    ? evidence != null
                        ? $"{folder} — contains {evidence}."
                        : $"{folder} — no file there matches the game's name, so make "
                          + "sure this really is the right folder."
                    : $"London does not know where your {DisplayName} install is.",
                located ? null
                        : "Use the setup check's \"Locate my game folder…\" button.",
                Manifest.SteamAppId.Length > 0
                    ? $"https://store.steampowered.com/app/{Manifest.SteamAppId}/" : null));

            list.Add(ModComponent(folder));
        }
        else if (kind == "apworld_and_external_mod")
        {
            // Thunderstore / Steam Workshop: the mod lives in a mod-manager
            // profile or a subscription, which London cannot see into. A green
            // light here would be a guess, so the ceiling is a standing amber
            // reminder that never blocks the launch.
            list.Add(new GameComponent(
                "In-game mod (Thunderstore / Workshop)", false, ComponentNeed.Optional,
                "London cannot see mod-manager installs, so it cannot confirm the "
              + "Archipelago mod is in place.",
                "Set the mod up as its author describes before playing — otherwise "
              + "the game starts but never sends a check.",
                Manifest.ModUrl ?? Manifest.ReleasePage));
        }
        else if (kind is "manual" or "discord_only")
        {
            list.Add(new GameComponent(
                "Manual setup", false, ComponentNeed.Optional,
                "This game is set up by hand — London cannot check it.",
                "Follow the author's steps (setup check on this page) before playing.",
                Manifest.ReleasePage.Length > 0 ? Manifest.ReleasePage : null));
        }

        return list;
    }

    /// The mod's state, told apart honestly:
    ///   green — our receipt exists and every file it lists is on disk, or a
    ///           hand-made install of the same files is recognisably present;
    ///   red   — we know it is absent or broken (blocks launch: a modless game
    ///           runs fine and sends nothing, the worst failure there is);
    ///   amber — we genuinely cannot tell (a .7z we cannot look inside).
    private GameComponent ModComponent(string? gameFolder)
    {
        const string name = "Archipelago mod in the game";

        if (gameFolder == null)
            return new GameComponent(name, false, ComponentNeed.Required,
                "Waiting for your game folder — the mod goes inside it.",
                "Locate the game folder first, then click Install.");

        var receipt = PcSetup.ReadReceipt(ModReceiptPath);
        if (receipt != null)
        {
            var missing = receipt.Files
                .Where(f => !File.Exists(Path.Combine(gameFolder, f.Path))).ToList();
            return missing.Count == 0
                ? new GameComponent(name, true, ComponentNeed.Required,
                    $"Installed by London — {receipt.Files.Count} file(s) present in "
                  + "your game folder.")
                : new GameComponent(name, false, ComponentNeed.Required,
                    $"{missing.Count} of {receipt.Files.Count} installed mod files are "
                  + $"gone (first: {missing[0].Path}).",
                    "An antivirus may have removed them — whitelist the game folder, "
                  + "then run Install again to restore them.");
        }

        string archive = DownloadedModPath;
        bool haveArchive = archive.Length > 0 && File.Exists(archive);

        if (haveArchive && !archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            // Downloaded but unreadable to us: after the player copies it in by
            // hand there is nothing London can measure, so this can never go
            // green — it stays an amber "make sure", which is the truth.
            return new GameComponent(name, false, ComponentNeed.Optional,
                "The mod archive is downloaded, but London cannot look inside a "
              + $"{Path.GetExtension(archive)} file to confirm the install.",
                $"Unpack {archive} into your game folder as the author describes.");

        if (haveArchive && PcSetup.HandInstallLooksPresent(gameFolder, archive))
            return new GameComponent(name, true, ComponentNeed.Required,
                "The mod's files are in your game folder (added by hand — London "
              + "found them by name, so it cannot vouch for their version).");

        return new GameComponent(name, false, ComponentNeed.Required,
            haveArchive
                ? "The mod is downloaded but its files are not in your game folder yet."
                : "The mod is not installed yet.",
            haveArchive
                ? "Open the setup check — it opens the downloaded file and your game "
                  + "folder side by side."
                : "Click Install — London fetches the mod from the author's release.");
    }

    /// The walkthrough exists for every kind where part of the job can be the
    /// player's. The two fully-automatic kinds have nothing to walk through.
    public bool HasComponentSetup => Manifest.Install is not ("apworld_only" or "bundled");

    public void ShowComponentSetup(System.Windows.Window? owner)
        => PcSetupDialog.Show(owner, this, Manifest,
                              RegisteredGameFolder, DownloadedModPath,
                              folder =>
                              {
                                  // Persisted under the same key the launcher's
                                  // own locate flow uses — one truth, two doors.
                                  var s = SettingsStore.Load();
                                  s.OriginalGameLocations[GameId] = folder;
                                  SettingsStore.Save(s);
                              });

    /// One visible door to the walkthrough on the game's page, available
    /// BEFORE install — that is exactly when a manual game needs it.
    public IReadOnlyList<GameCommand> GetCommands()
        => HasComponentSetup
            ? new[]
              {
                  new GameCommand("🧭  Setup check",
                      "See what is in place, what is missing, and fix each piece — "
                    + "London installs what it can and shows the author's steps for "
                    + "the rest.",
                      owner => ShowComponentSetup(owner),
                      NeedsInstall: false),
              }
            : Array.Empty<GameCommand>();

    /// The author's own words, when London cannot do the whole job. Never a
    /// paraphrase: a rewritten setup guide is a way to be subtly wrong about
    /// somebody else's game.
    public UIElement? CreateSettingsPanel()
    {
        if (Manifest.Steps.Length == 0 && Manifest.ModUrl == null) return null;

        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 6),
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            // CleanSteps: some harvested "steps" are the Archipelago project's
            // own README (a fork served it as the game's). Showing that would
            // be showing garbage AS the author's guidance.
            Text = PcSetup.CleanSteps(Manifest.Steps)
                   ?? (Manifest.ReleasePage.Length > 0
                        ? "The author has not published setup steps. Their release "
                        + $"page is {Manifest.ReleasePage}."
                        : "The author has not published setup steps."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.85,
        });
        return stack;
    }

    // -------------------------------------------------------------- running

    public bool IsRunning { get; private set; }

    public event Action<long[]>? LocationsChecked;
    public event Action<long[]>? LocationsMissing;
    public event Action? GoalCompleted;
    public event Action<string>? StandaloneItemReceived;
    public event Action<string>? LogLine;
    public event Action<int>? GameExited;

    public Func<JsonElement?>? GetSlotData { get; set; }
    public Func<long[]?>? GetServerLocations { get; set; }
    public Func<int>? GetOwnSlot { get; set; }
    public Func<string?>? GetSeedName { get; set; }
    public string? LastSlotName { get; set; }

    /// Starts the game and tells the player what to type into its own connect
    /// screen. The mod inside the game owns the AP connection from there; the
    /// launcher does not intercept it, and says so rather than showing a
    /// progress bar over somebody else's socket.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string where = session.ServerUri;
        LogLine?.Invoke($"[{DisplayName}] {DisplayName} connects to Archipelago from "
                      + $"inside the game. In its Archipelago screen, enter:");
        LogLine?.Invoke($"[{DisplayName}]   server: {where}");
        LogLine?.Invoke($"[{DisplayName}]   slot:   {session.SlotName}");

        try
        {
            if (Manifest.SteamAppId.Length > 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{Manifest.SteamAppId}",
                    UseShellExecute = true,
                });
                IsRunning = true;
                LogLine?.Invoke($"[{DisplayName}] Asked Steam to start the game.");
            }
            else
            {
                LogLine?.Invoke($"[{DisplayName}] Start the game yourself — London does "
                              + "not know where this one is installed.");
            }
        }
        catch (Exception e)
        {
            LogLine?.Invoke($"[{DisplayName}] Could not start it: {e.Message}");
        }

        LastSlotName = session.SlotName;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // London did not take the game over, so it does not get to close it.
        // Saying "stopped" would only be true of our own bookkeeping.
        IsRunning = false;
        GameExited?.Invoke(0);
        return Task.CompletedTask;
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    /// Quiets the compiler about events this kind of game never raises: the
    /// mod inside the game reports its own checks straight to the server.
    protected void NeverRaised()
    {
        LocationsChecked?.Invoke(Array.Empty<long>());
        LocationsMissing?.Invoke(Array.Empty<long>());
        GoalCompleted?.Invoke();
        StandaloneItemReceived?.Invoke("");
    }
}

// ---------------------------------------------------------------------------
// The setup logic, kept pure and static so a proof harness can drive it with
// folders and entry lists — the negative tests ("this is NOT the game", "this
// zip is NOT recognisable") have to be runnable without a window.
// ---------------------------------------------------------------------------
public static class PcSetup
{
    // ------------------------------------------------------------- URLs ----

    /// Mirrors the catalogue packer's rule for what lands in the plugin.json
    /// `declares` block. The two MUST agree: this class may only ever fetch
    /// addresses the consent screen already named.
    public static bool IsDirectFileUrl(string? url)
    {
        if (url == null) return false;
        string name = url.Rsplit().ToLowerInvariant();
        return name.EndsWith(".zip", StringComparison.Ordinal)
            || name.EndsWith(".7z", StringComparison.Ordinal)
            || name.EndsWith(".dll", StringComparison.Ordinal);
    }

    public static bool IsZipUrl(string? url)
        => url != null
           && url.Rsplit().EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// The last path segment — the filename a release asset URL names.
    public static string Rsplit(this string url)
    {
        int i = url.LastIndexOf('/');
        return i < 0 ? url : url[(i + 1)..];
    }

    public static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
        return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }

    public static bool LooksLikeZip(byte[] data)
        => data.Length >= 4 && data[0] == 'P' && data[1] == 'K';

    // ------------------------------------------------------- steps text ----

    /// Null when the harvested "steps" are not the author's steps at all.
    ///
    /// ⚠ 11 games' steps were the Archipelago PROJECT README — harvest_pc.py
    /// asked a fork for /readme and forks serve the upstream file, which opens
    /// with the project header and a list of eighty unrelated games. Shown on
    /// a game page it reads as insane. Detect that exact shape; a real guide
    /// that merely mentions Archipelago passes.
    public static string? CleanSteps(string steps)
    {
        if (string.IsNullOrWhiteSpace(steps)) return null;
        string low = steps.ToLowerInvariant();
        bool upstream =
            low.Contains("archipelago provides a generic framework")
            || low.Contains("currently, the following games are supported")
            || low.TrimStart().StartsWith("# [archipelago](https://archipelago.gg)",
                                          StringComparison.Ordinal);
        return upstream ? null : steps;
    }

    // -------------------------------------------------- folder evidence ----

    /// Does the folder contain any program at all (top level or one level
    /// down)? Bounded: a picked folder can be a whole drive, and validation
    /// must never hang the picker.
    public static bool HasExecutable(string folder)
    {
        try
        {
            if (Directory.EnumerateFiles(folder, "*.exe").Any()) return true;
            foreach (var sub in Directory.EnumerateDirectories(folder).Take(64))
                if (Directory.EnumerateFiles(sub, "*.exe").Any()) return true;
        }
        catch (Exception) { /* unreadable = no evidence */ }
        return false;
    }

    /// The first file or folder name (two levels deep) sharing a meaningful
    /// token with the game's title — "Blasphemous.exe" for Blasphemous,
    /// "oriDE.exe" for Ori. Null when nothing matches; the caller words the
    /// doubt, because absence here is a warning, not proof of a wrong folder.
    public static string? NameEvidence(string folder, string gameName)
    {
        var tokens = gameName.Split(' ', ':', '-', '\'', '!', '.', ',')
            .Where(t => t.Length >= 4)            // "the", "of", "II" prove nothing
            .Select(t => t.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0) return null;

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         folder, "*", SearchOption.TopDirectoryOnly).Take(256)
                     .Concat(Directory.EnumerateDirectories(folder).Take(32)
                         .SelectMany(d => SafeEntries(d).Take(64))))
            {
                string name = Path.GetFileName(path).ToLowerInvariant();
                if (tokens.Any(name.Contains))
                    return Path.GetFileName(path);
            }
        }
        catch (Exception) { }
        return null;
    }

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------ the receipt ----

    public sealed record ReceiptFile(string Path, long Size);
    public sealed record Receipt(string ModUrl, string Version,
                                 IReadOnlyList<ReceiptFile> Files);

    public static Receipt? ReadReceipt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var files = new List<ReceiptFile>();
            if (r.TryGetProperty("files", out var arr))
                foreach (var f in arr.EnumerateArray())
                    files.Add(new ReceiptFile(
                        f.GetProperty("path").GetString() ?? "",
                        f.TryGetProperty("size", out var s) ? s.GetInt64() : 0));
            return new Receipt(
                r.TryGetProperty("mod_url", out var u) ? u.GetString() ?? "" : "",
                r.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                files);
        }
        catch (Exception) { return null; }   // a corrupt receipt = no receipt
    }

    public static void WriteReceipt(string path, Receipt receipt)
    {
        var obj = new
        {
            mod_url = receipt.ModUrl,
            version = receipt.Version,
            files = receipt.Files.Select(f => new { path = f.Path, size = f.Size }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------- zip layout plans ----

    /// The verdict on one archive: either a concrete list of (zip entry →
    /// path relative to the game folder), or a Reason the archive was left to
    /// the player. Never both, never a guess.
    public sealed record ZipPlan(
        IReadOnlyList<(string Entry, string DestRel)> Files,
        IReadOnlyList<string> SkippedExisting,
        string? Reason)
    {
        public static ZipPlan Fallback(string reason)
            => new(Array.Empty<(string, string)>(), Array.Empty<string>(), reason);
    }

    /// Loader trees whose root the archive may overlay directly onto the game
    /// folder. Everything the tree needs lives under its own directory.
    private static readonly string[] OverlayRoots =
        // QMods is the convention TerraTech and the QModManager family use --
        // a folder per mod, dropped beside the executable, no loader tree to
        // install first. Adding it here is what lets London place such a mod
        // instead of refusing because it was looking for BepInEx.
        { "BepInEx", "MelonLoader", "Mods", "UserData", "QMods" };

    /// Root-level files a loader overlay legitimately ships next to its tree:
    /// the doorstop/proxy bootstraps and the usual paperwork. Anything else at
    /// the root makes the layout unrecognisable — by design, because a zip
    /// that writes arbitrary root files is a zip that can clobber the game.
    private static readonly string[] AllowedRootFiles =
    {
        "winhttp.dll", "version.dll", "doorstop_config.ini", ".doorstop_version",
        "libdoorstop.so", "run_bepinex.sh", "changelog.txt",
    };

    private static bool AllowedRootFile(string name)
        => AllowedRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
           || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// Decide where a zip's files belong, if that can be decided at all.
    ///
    ///   1. BepInEx / MelonLoader tree  → overlaid onto the game folder root.
    ///   2. plugins/patchers/config/core tree → under an EXISTING BepInEx/.
    ///   3. bare plugin dll(s)          → into an EXISTING BepInEx/plugins/.
    ///   Anything else → fallback with the reason spelled out.
    ///
    /// Overwrite rules: a planned file may replace one already on disk only
    /// when we put it there (the receipt says so) or it is a loader file that
    /// updating the mod legitimately replaces. An existing .exe is never
    /// overwritten; an existing config the player may have edited is skipped
    /// and reported, not clobbered.
    public static ZipPlan PlanZip(
        IReadOnlyList<(string FullName, long Length)> entries,
        string gameFolder,
        IReadOnlySet<string> ownedFiles)
    {
        // Files only; zip directory entries end with '/'.
        var files = entries
            .Where(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"))
            .Select(e => (Name: e.FullName.Replace('\\', '/'), e.Length))
            .ToList();
        if (files.Count == 0) return ZipPlan.Fallback("the archive is empty");

        // Zip-slip guard: one hostile path poisons the whole archive.
        if (files.Any(f => f.Name.Contains("..") || f.Name.Contains(':')
                           || f.Name.StartsWith("/")))
            return ZipPlan.Fallback("it contains unsafe file paths");

        // Strip a single wrapper folder ("MyMod-1.2/…") when every entry sits
        // inside it and the wrapper is not itself a meaningful root.
        string? wrapper = null;
        var firstSegs = files.Select(f => f.Name.Split('/')[0]).Distinct().ToList();
        if (firstSegs.Count == 1 && files.All(f => f.Name.Contains('/')))
        {
            string seg = firstSegs[0];
            bool meaningful = OverlayRoots.Concat(new[] { "plugins", "patchers", "config", "core" })
                .Contains(seg, StringComparer.OrdinalIgnoreCase);
            if (!meaningful) wrapper = seg + "/";
        }

        var inner = files
            .Select(f => (Name: wrapper != null ? f.Name[wrapper.Length..] : f.Name, f.Length))
            .Where(f => f.Name.Length > 0)
            .ToList();

        var roots = inner.Select(f => f.Name.Split('/')[0]).Distinct().ToList();

        List<(string Entry, string DestRel)> Map(Func<string, string> dest)
            => inner.Select(f => ((wrapper ?? "") + f.Name, dest(f.Name)))
                    .Select(t => (t.Item1, t.Item2.Replace('/', Path.DirectorySeparatorChar)))
                    .ToList();

        List<(string Entry, string DestRel)>? planned = null;

        // 1. A loader tree overlaid on the game root.
        if (roots.Any(r => OverlayRoots.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            var strayRoot = inner.Where(f => !f.Name.Contains('/'))
                                 .FirstOrDefault(f => !AllowedRootFile(f.Name));
            if (strayRoot.Name != null)
                return ZipPlan.Fallback(
                    $"it wants to write \"{strayRoot.Name}\" at the game's root, "
                  + "which is not part of a mod-loader layout");
            var strayDir = roots.FirstOrDefault(r =>
                inner.Any(f => f.Name.StartsWith(r + "/"))
                && !OverlayRoots.Contains(r, StringComparer.OrdinalIgnoreCase));
            if (strayDir != null)
                return ZipPlan.Fallback(
                    $"it mixes a mod-loader tree with an unknown \"{strayDir}\" folder");
            planned = Map(n => n);
        }
        // 2. A plugins/patchers/config tree that presumes an installed BepInEx.
        else if (roots.All(r => inner.Any(f => f.Name.StartsWith(r + "/"))
                                || AllowedRootFile(r))
                 && roots.Any(r => r.Equals("plugins", StringComparison.OrdinalIgnoreCase)
                                || r.Equals("patchers", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Directory.Exists(Path.Combine(gameFolder, "BepInEx")))
                return ZipPlan.Fallback(
                    "it is a BepInEx plugins tree but your game folder has no "
                  + "BepInEx yet — install BepInEx first, as the author describes");
            bool known(string r) => r is "plugins" or "patchers" or "config" or "core"
                                    || AllowedRootFile(r);
            var unknown = roots.FirstOrDefault(r => !known(r.ToLowerInvariant()));
            if (unknown != null)
                return ZipPlan.Fallback($"its \"{unknown}\" folder is not one that "
                                      + "belongs under BepInEx");
            planned = Map(n => n.Contains('/') ? "BepInEx/" + n : n);
        }
        // 3. Bare dll(s) — a plugin without its packaging.
        else if (inner.All(f => !f.Name.Contains('/'))
                 && inner.Any(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                 && inner.All(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                               || f.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                               || AllowedRootFile(f.Name)))
        {
            if (!Directory.Exists(Path.Combine(gameFolder, "BepInEx", "plugins")))
                return ZipPlan.Fallback(
                    "it is a bare plugin dll but your game folder has no "
                  + "BepInEx\\plugins yet — install BepInEx first, as the author "
                  + "describes");
            planned = Map(n => "BepInEx/plugins/" + n);
        }
        else
        {
            return ZipPlan.Fallback(
                "its top level (" + string.Join(", ", roots.Take(4))
                + ") is not a mod-loader layout London knows");
        }

        // Overwrite audit, file by file. This runs AFTER a layout is
        // recognised because only then do destinations exist to audit.
        var final   = new List<(string Entry, string DestRel)>();
        var skipped = new List<string>();
        foreach (var (entry, destRel) in planned)
        {
            string dest = Path.Combine(gameFolder, destRel);
            if (!File.Exists(dest)) { final.Add((entry, destRel)); continue; }

            if (ownedFiles.Contains(destRel)) { final.Add((entry, destRel)); continue; }

            string leaf = Path.GetFileName(destRel);
            string rel  = destRel.Replace('\\', '/');
            if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ZipPlan.Fallback(
                    $"it would overwrite \"{leaf}\", an existing program in your "
                  + "game folder");

            // A config the player may have edited: keep theirs, say so.
            if (rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            { skipped.Add(destRel); continue; }

            // Loader files may be replaced: everything under a loader tree, and
            // the bootstrap dlls at the root, are what a mod update legitimately
            // rewrites — that is what updating IS. Anything ELSE that already
            // exists and is not ours (the game's own changelog.txt, say) is kept
            // and reported, never silently clobbered.
            bool underLoaderTree = OverlayRoots.Any(rt =>
                rel.StartsWith(rt + "/", StringComparison.OrdinalIgnoreCase));
            bool bootstrap = AllowedRootFiles.Contains(leaf, StringComparer.OrdinalIgnoreCase);
            if (underLoaderTree || bootstrap) { final.Add((entry, destRel)); continue; }

            skipped.Add(destRel);
        }

        return new ZipPlan(final, skipped, null);
    }

    /// Carry out a plan PlanZip approved: extract each entry beside its
    /// destination and move it into place, so a failure mid-file can never
    /// leave a half-written dll where a whole one stood. Returns what was
    /// placed, for the receipt — the receipt is how the checker later answers
    /// "is the mod still intact?" without guessing.
    public static List<ReceiptFile> ApplyPlan(string archivePath, string gameFolder,
                                              ZipPlan plan, CancellationToken ct)
    {
        var installed = new List<ReceiptFile>();
        using var za = ZipFile.OpenRead(archivePath);
        foreach (var (entryName, destRel) in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = za.GetEntry(entryName)
                ?? throw new InvalidDataException(
                    $"The archive changed under us — \"{entryName}\" is gone.");
            string dest = Path.Combine(gameFolder, destRel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string part = dest + ".part";
            entry.ExtractToFile(part, overwrite: true);
            File.Move(part, dest, overwrite: true);
            installed.Add(new ReceiptFile(destRel, entry.Length));
        }
        return installed;
    }

    // --------------------------- recognising a hand-made install -----------

    private static readonly object ScanLock = new();
    private static readonly Dictionary<string, (DateTime At, bool Found)> ScanCache = new();

    /// True when the game folder visibly contains the archive's plugin dlls —
    /// the honest way to verify an install the player did themselves, without
    /// pretending to know where the files "should" be. Cached briefly: this is
    /// read on page draws, and a game folder can be enormous.
    public static bool HandInstallLooksPresent(string? gameFolder, string archivePath)
    {
        if (gameFolder == null || archivePath.Length == 0
            || !File.Exists(archivePath)
            || !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        string key = gameFolder + "|" + archivePath;
        lock (ScanLock)
            if (ScanCache.TryGetValue(key, out var hit)
                && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30))
                return hit.Found;

        bool found = false;
        try
        {
            List<string> wanted;
            using (var za = ZipFile.OpenRead(archivePath))
                wanted = za.Entries
                    .Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name.ToLowerInvariant())
                    .Distinct().ToList();

            if (wanted.Count > 0)
            {
                var present = new HashSet<string>();
                int walked = 0;
                foreach (var f in Directory.EnumerateFiles(
                             gameFolder, "*.dll", SearchOption.AllDirectories))
                {
                    if (++walked > 6000) break;   // bounded; a partial walk can
                                                  // only miss, never invent
                    present.Add(Path.GetFileName(f).ToLowerInvariant());
                }
                // Every plugin dll the author shipped, found by name. Half a
                // mod is exactly the state that runs and sends nothing.
                found = wanted.All(present.Contains);
            }
        }
        catch (Exception) { found = false; }

        lock (ScanLock) ScanCache[key] = (DateTime.UtcNow, found);
        return found;
    }
}

// ---------------------------------------------------------------------------
// The guided setup dialog — the D2 component-wizard idea for PC games: one
// clear statement per component, coloured by state, with the fix right next
// to the thing complaining. Built in code because plugins ship no XAML.
// ---------------------------------------------------------------------------
internal static class PcSetupDialog
{
    private static readonly System.Windows.Media.Color Green =
        System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly System.Windows.Media.Color Amber =
        System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly System.Windows.Media.Color Red =
        System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44);

    public static void Show(Window? owner, GenericPcPlugin plugin, PcManifest manifest,
                            string? gameFolder, string downloadedMod,
                            Action<string> persistFolder)
    {
        var win = new Window
        {
            Title = $"{plugin.DisplayName} — setup check",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x22)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0)),
            ResizeMode = ResizeMode.NoResize,
        };

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        win.Content = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Content = root,
        };

        var status = new System.Windows.Controls.TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        void Redraw()
        {
            root.Children.Clear();

            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "WHAT LONDON CAN SEE",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var components = plugin.DetectComponents();
            bool ready = true;
            foreach (var c in components)
            {
                bool blocking = !c.Present && c.Need == ComponentNeed.Required;
                if (blocking) ready = false;

                var tint = c.Present ? Green : blocking ? Red : Amber;
                var row = new System.Windows.Controls.StackPanel
                { Margin = new Thickness(0, 0, 0, 10) };

                var head = new System.Windows.Controls.StackPanel
                { Orientation = System.Windows.Controls.Orientation.Horizontal };
                head.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = new System.Windows.Media.SolidColorBrush(tint),
                    Margin = new Thickness(0, 3, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                });
                head.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = c.Name,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12.5,
                    Foreground = win.Foreground,
                });
                row.Children.Add(head);
                row.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = c.Status + (c.Advice != null ? "\n" + c.Advice : ""),
                    FontSize = 11.5,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(18, 2, 0, 0),
                    Foreground = win.Foreground,
                });
                root.Children.Add(row);
            }

            // The verdict line the whole dialog exists for: pass = say so,
            // fail = the red rows above already say which piece and why.
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = ready
                    ? "✓ Ready to play — every check London can make passes."
                    : "Not ready yet — fix the red items above.",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 12),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    ready ? Green : Red),
                TextWrapping = TextWrapping.Wrap,
            });

            // --- The author's steps, verbatim or honestly absent ---
            string? steps = PcSetup.CleanSteps(manifest.Steps);
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "THE AUTHOR'S STEPS",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 6),
            });
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = steps ?? "The author has not published setup steps"
                       + (manifest.ReleasePage.Length > 0
                              ? " — their release page is the place to look." : "."),
                FontSize = 11.5,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 180,
                Foreground = win.Foreground,
            });

            // --- Actions, only the ones that make sense right now ---
            var actions = new System.Windows.Controls.WrapPanel
            { Margin = new Thickness(0, 14, 0, 0) };
            root.Children.Add(actions);
            root.Children.Add(status);

            void AddButton(string label, string tip, Action run)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = label,
                    ToolTip = tip,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 8, 8),
                };
                b.Click += (_, _) =>
                {
                    try { run(); }
                    catch (Exception ex) { status.Text = ex.Message; }
                };
                actions.Children.Add(b);
            }

            gameFolder = ReadFolder();

            bool folderKind = manifest.Install is "apworld_and_mod" or "mod_package";
            if (folderKind)
                AddButton(gameFolder == null ? "Locate my game folder…"
                                             : "Change game folder…",
                    "Point London at the folder where the game itself is installed. "
                  + "Your install is only ever added to, never replaced.",
                    () =>
                    {
                        var dlg = new Microsoft.Win32.OpenFolderDialog
                        { Title = $"Locate your {plugin.DisplayName} install folder" };
                        if (dlg.ShowDialog(win) != true) return;
                        // The same validation the launcher's own picker runs —
                        // one rule, wherever the folder comes in.
                        if (plugin.ValidateExistingInstall(dlg.FolderName) is { } why)
                        { status.Text = why; return; }
                        persistFolder(dlg.FolderName);
                        status.Text = "";
                        Redraw();
                    });

            bool installable = manifest.Install is not ("manual" or "discord_only")
                               && plugin.DetectComponents()
                                        .Any(c => !c.Present && c.Need == ComponentNeed.Required);
            if (installable)
                AddButton("Install what London can",
                    "Downloads the world (and the mod, when its layout is "
                  + "recognisable) from the author's release, then re-checks.",
                    async () =>
                    {
                        foreach (System.Windows.Controls.Button b in
                                 actions.Children.OfType<System.Windows.Controls.Button>())
                            b.IsEnabled = false;
                        try
                        {
                            var prog = new Progress<(int Pct, string Msg)>(
                                p => status.Text = p.Msg);
                            await plugin.InstallOrUpdateAsync(prog);
                        }
                        catch (Exception ex) { status.Text = ex.Message; }
                        Redraw();
                    });

            if (gameFolder != null)
                AddButton("Open game folder",
                    "Opens your game's install folder in Explorer.",
                    () => Process.Start(new ProcessStartInfo
                    { FileName = gameFolder, UseShellExecute = true }));

            if (downloadedMod.Length > 0 && File.Exists(downloadedMod))
                AddButton("Open downloaded mod",
                    "Shows the mod archive London downloaded, so you can unpack "
                  + "it into the game folder as the author describes.",
                    () => Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{downloadedMod}\"",
                        UseShellExecute = true,
                    }));

            if (manifest.ReleasePage.Length > 0)
                AddButton("Open the author's page ↗",
                    manifest.ReleasePage,
                    () => Process.Start(new ProcessStartInfo
                    { FileName = manifest.ReleasePage, UseShellExecute = true }));

            AddButton("Close", "", () => win.Close());
        }

        string? ReadFolder()
        {
            var s = SettingsStore.Load();
            return s.OriginalGameLocations.TryGetValue(plugin.GameId, out var f)
                   && !string.IsNullOrWhiteSpace(f) && Directory.Exists(f) ? f : null;
        }

        Redraw();
        win.ShowDialog();
    }
}

/// The embedded pc.json, parsed. Every field is either present or explicitly
/// empty — the plugin says "not known" rather than inventing a value.
public sealed record PcManifest(
    string Id,
    string DisplayName,
    string Subtitle,
    string ApWorldName,
    string Description,
    string Install,
    string ApworldFileName,
    string? ApworldUrl,
    string? ModUrl,
    string ReleasePage,
    string RepoUrl,
    string? WorldBy,
    string? Licence,
    string SteamAppId,
    string Version,
    string Steps)
{
    public static PcManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        static string S(JsonElement e, string k)
            => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() ?? "" : "";
        static string? N(JsonElement e, string k)
        {
            string s = S(e, k);
            return s.Length == 0 ? null : s;
        }

        string id = S(r, "id");
        return new PcManifest(
            id,
            S(r, "display_name"),
            S(r, "subtitle"),
            S(r, "ap_world_name"),
            S(r, "description"),
            S(r, "install"),
            S(r, "apworld_file") is { Length: > 0 } f ? f : id + ".apworld",
            N(r, "apworld_url"),
            N(r, "mod_url"),
            S(r, "release_page"),
            S(r, "repo_url"),
            N(r, "world_by"),
            N(r, "licence"),
            S(r, "steam_appid"),
            S(r, "version"),
            S(r, "steps"));
    }
}
