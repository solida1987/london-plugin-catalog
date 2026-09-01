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

    // --- Mission Control (session_window games; StarCraft 2 first) ---
    // The board draws from these; they fill through the ordinary plugin
    // callbacks, so no new launcher API was needed for the data.
    private IReadOnlyDictionary<string, long>? _mcLocationTable;
    private readonly HashSet<long> _mcChecked = new();
    private Sc2MissionWindow? _mcWindow;
    private Sc2Bridge? _mcBridge;
    private string? _mcAuth;    // "slot:password", escaped — the connect URI's userinfo
    private string? _mcServer;  // host:port

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
        WireRelay();
    }

    /// For proofs. The public constructor reads the embedded pc.json, which
    /// means one assembly can only ever BE one game — but the negative tests
    /// ("does a .7z game refuse to claim AutoMod?") need one harness to wear
    /// every install kind in turn. Injection is for test harnesses only; a
    /// shipped plugin always carries its manifest inside itself.
    protected GenericPcPlugin(PcManifest manifest)
    {
        Manifest = manifest;
        WireRelay();
    }

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

    public string[] GameBadges => Manifest.StandaloneGame
        ? new[] { "PC", "London installs the game" }
        : Manifest.Install switch
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
        // London downloads the game itself, so there is no folder to point at
        // and nothing for the player to fetch. One button, start to finish.
        _ when Manifest.StandaloneGame && ModIsAppliableArchive
                       => InstallCapability.AutoInstall,
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
    /// Where a game London provides itself lives. Under the plugin's own
    /// folder, so uninstalling the plugin takes the game with it and nothing
    /// of the player's is ever touched.
    private string OwnGameFolder => Path.Combine(GameDirectory, "game");

    private string? RegisteredGameFolder
    {
        get
        {
            // ⚠ London PROVIDES this game, so there is nothing to point at and
            // nothing to auto-find. Asking the player for a folder here is what
            // made Ship of Harkinian uninstallable: the setup check demanded a
            // copy of a game that only exists inside the download.
            if (Manifest.StandaloneGame)
            {
                Directory.CreateDirectory(OwnGameFolder);
                return OwnGameFolder;
            }

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
        // A named lookup the game itself vouches for beats any guess, so it
        // goes first.
        string? told = LocatedByGame();
        if (told != null) return told;

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

    /// The install path the GAME recorded for itself, when the manifest names
    /// a lookup for it. Null when there is no locator, the file is absent, or
    /// it does not point at something that looks like the game -- never a
    /// guess dressed up as an answer.
    ///
    /// ⚠ Battle.net games are invisible to the Steam locator and are not
    /// installed under any of the usual roots (this one was measured on
    /// F:\Spil\StarCraft II), so without this London asks the player for a
    /// folder it could simply have read.
    private string? LocatedByGame()
    {
        if (!string.Equals(Manifest.LocatorKind, "sc2_executeinfo",
                           StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Same file, same shape as the world's own client reads:
            //     executable = <root>\Versions\BaseNNNNN\SC2_x64.exe
            string info = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "StarCraft II", "ExecuteInfo.txt");
            if (!File.Exists(info)) return null;

            string text = File.ReadAllText(info);
            int eq = text.IndexOf('=');
            if (eq < 0) return null;
            string exe = text[(eq + 1)..].Trim();

            // Everything above "Versions" IS the install root.
            int cut = exe.IndexOf(@"\Versions", StringComparison.OrdinalIgnoreCase);
            if (cut <= 0) return null;
            string root = exe[..cut];

            return LooksLikeAGame(root) ? root : null;
        }
        catch { return null; }
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
        // A bundled world with a data package is only really installed once
        // that package is on disk -- saying "installed" while the maps are
        // missing is how a player ends up pressing Play into a client that
        // immediately tells them to run /download_data.
        "bundled" => Manifest.DataRepo.Length > 0
            ? RegisteredGameFolder is { } gf
              && Manifest.DataMarker.Length > 0
              && Path.Exists(Path.Combine(gf, Manifest.DataMarker))
            : CustomWorldsDir() != null,
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

    /// Fetch the world's data package and unpack it into the GAME's folder.
    ///
    /// ⛔ WE DISTRIBUTE NOTHING. This is the same file, from the same release,
    /// that the world's own client downloads when the player types
    /// /download_data into its console -- London just does it up front, from
    /// the project's own repository, so the player never meets that console.
    ///
    /// ⚠ Pinned to a TAG, not "latest". These releases are API versions: the
    /// newest one belongs to a newer apworld than the one installed, and
    /// taking it would hand the game maps its client cannot read.
    private async Task InstallGameDataAsync(IProgress<(int Pct, string Msg)> progress,
                                            CancellationToken ct)
    {
        string? folder = RegisteredGameFolder;
        if (folder == null)
        {
            progress?.Report((100,
                $"{DisplayName} ships with Archipelago, but London needs to know where "
              + "the game itself is before it can add the Archipelago maps and mods. "
              + "Point at it in Settings, then press Install again."));
            return;
        }

        if (Manifest.DataMarker.Length > 0
            && Path.Exists(Path.Combine(folder, Manifest.DataMarker)))
        {
            progress?.Report((100, $"The Archipelago data is already in {folder}."));
            return;
        }

        string url = $"https://github.com/{Manifest.DataRepo}/releases/download/"
                   + $"{Manifest.DataTag}/{Manifest.DataAsset}";

        // ⚠ Streamed to a file, never into memory: this package is hundreds of
        // megabytes and a byte[] of it is a needless spike in a launcher that
        // is already holding a game library.
        string tmp = Path.Combine(Path.GetTempPath(),
                                  $"{Manifest.Id}-{Manifest.DataTag}-{Manifest.DataAsset}");
        try
        {
            progress?.Report((5, $"Fetching {Manifest.DataAsset} ({Manifest.DataTag})…"));
            await PcSetup.DownloadToFileAsync(url, tmp,
                p => progress?.Report((5 + p * 80 / 100,
                        $"Fetching {Manifest.DataAsset} — {p}%")), ct)
                .ConfigureAwait(false);

            progress?.Report((88, $"Unpacking into {folder}…"));
            using (var zip = System.IO.Compression.ZipFile.OpenRead(tmp))
            {
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    // Normalise BEFORE the folder test: a zip written with
                    // backslash entries (Azahar's was; a foreign data pack can
                    // be too) otherwise fails every check below and installs
                    // nothing, silently.
                    string rel = entry.FullName.Replace('\\', '/');
                    if (rel.EndsWith('/')) continue;

                    // Refuse anything that would land outside the game folder.
                    string target = Path.GetFullPath(
                        Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            // The receipt the world's client checks. Without it the client
            // opens with "your map files may be outdated (version number not
            // found)" on every single connect — about files London just
            // installed. Best-effort: a missing receipt only re-shows that
            // notice, so it must never fail the install.
            if (Manifest.DataMetadataFile.Length > 0)
                await WriteDataReceiptAsync(folder, ct).ConfigureAwait(false);

            progress?.Report((100, $"Archipelago maps and mods installed into {folder}."));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp */ }
        }
    }

    /// Write the metadata file the world's client uses to judge whether the
    /// data package is current.
    ///
    /// Measured against worlds/sc2/client.py (0.6.7): /download_data stores
    /// the GitHub release object for the pinned tag as Python's str(dict),
    /// and the update check is a plain string comparison against a fresh
    /// fetch. cleanup_downloaded_metadata KEEPS the assets array and deletes
    /// only each asset's volatile "download_count" — the first version of
    /// this receipt dropped the whole assets key, and the client answered
    /// with "Update for required files found" about files it had just been
    /// given. So: Python repr form, single-quoted strings, True/False/None,
    /// download_count omitted wherever it appears. A byte that differs only
    /// re-shows the client's notice, which /download_data then heals.
    private async Task WriteDataReceiptAsync(string folder, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
            string url = $"https://api.github.com/repos/{Manifest.DataRepo}"
                       + $"/releases/tags/{Manifest.DataTag}";
            string json = await http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var sb = new System.Text.StringBuilder();
            PyRepr(doc.RootElement, sb);

            await File.WriteAllTextAsync(
                Path.Combine(folder, Manifest.DataMetadataFile),
                sb.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogLine?.Invoke($"[{DisplayName}] Data receipt not written ({e.Message}) — "
                          + "the game client may show a harmless 'may be outdated' note.");
        }
    }

    /// Python's str() of a decoded JSON value, faithfully enough for a
    /// release object: ordered dicts, single-quoted strings (double when the
    /// value contains a single quote and no double), True/False/None, and
    /// control characters escaped the way repr() escapes them.
    private static void PyRepr(System.Text.Json.JsonElement el,
                               System.Text.StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (var p in el.EnumerateObject())
                {
                    // The one key the client's cleanup deletes. In a GitHub
                    // release object it only occurs on assets, so dropping it
                    // wherever it appears matches the client's file exactly.
                    if (p.Name == "download_count") continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    PyStr(p.Name, sb);
                    sb.Append(": ");
                    PyRepr(p.Value, sb);
                }
                sb.Append('}');
                break;
            case System.Text.Json.JsonValueKind.Array:
                sb.Append('[');
                bool f2 = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!f2) sb.Append(", ");
                    f2 = false;
                    PyRepr(item, sb);
                }
                sb.Append(']');
                break;
            case System.Text.Json.JsonValueKind.String:
                PyStr(el.GetString() ?? "", sb);
                break;
            case System.Text.Json.JsonValueKind.Number:
                sb.Append(el.GetRawText());
                break;
            case System.Text.Json.JsonValueKind.True:  sb.Append("True");  break;
            case System.Text.Json.JsonValueKind.False: sb.Append("False"); break;
            default:                                   sb.Append("None");  break;
        }
    }

    private static void PyStr(string s, System.Text.StringBuilder sb)
    {
        // repr() prefers single quotes; flips to double only when the string
        // has a single quote and no double quote.
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        sb.Append(q);
        foreach (char c in s)
        {
            if (c == '\\')      sb.Append("\\\\");
            else if (c == q)    sb.Append('\\').Append(q);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == '\x7f')
                sb.Append("\\x").Append(((int)c).ToString("x2"));
            else sb.Append(c);
        }
        sb.Append(q);
    }

    private async Task InstallCoreAsync(IProgress<(int Pct, string Msg)> progress,
                                        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);

        if (Manifest.Install == "bundled")
        {
            // The world file ships with Archipelago, but the game may still
            // need a data package dropped beside it before anything works.
            if (Manifest.DataRepo.Length > 0)
            {
                await InstallGameDataAsync(progress, ct).ConfigureAwait(false);
                return;
            }
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
        await InstallLoaderAsync(progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- loader --
    // Some games load no outside code by themselves — TerraTech was measured
    // at zero references to any mod folder convention. For those the manifest
    // names a loader (BepInEx), fetched from ITS OWN official release, never
    // bundled with ours. Same posture as emulators: offered and named on the
    // consent screen, downloaded only from the address declared there.

    private bool LoaderDeclared =>
        string.Equals(Manifest.LoaderKind, "bepinex", StringComparison.Ordinal)
        && PcSetup.IsZipUrl(Manifest.LoaderUrl);

    /// The chainloader's own preloader is the proof: winhttp.dll alone is any
    /// number of things, but nothing else puts BepInEx/core/ in a game folder.
    private static bool LoaderPresent(string gameFolder)
        => File.Exists(Path.Combine(gameFolder, "winhttp.dll"))
        && File.Exists(Path.Combine(gameFolder, "BepInEx", "core", "BepInEx.Preloader.dll"));

    private async Task InstallLoaderAsync(IProgress<(int Pct, string Msg)>? progress,
                                          CancellationToken ct)
    {
        if (!LoaderDeclared) return;
        string? gameFolder = RegisteredGameFolder;
        if (gameFolder == null) return;             // the mod step already said so
        if (LoaderPresent(gameFolder)) return;      // theirs, or ours from last time

        progress?.Report((92, "Downloading BepInEx (the mod loader) from its own "
                            + "official release…"));
        byte[] data = await PcSetup.DownloadAsync(Manifest.LoaderUrl!, ct).ConfigureAwait(false);
        if (!PcSetup.LooksLikeZip(data))
            throw new InvalidDataException(
                "What came back from the BepInEx release address was not a zip. "
              + "Get it by hand from https://github.com/BepInEx/BepInEx/releases");

        string archive = Path.Combine(AppContext.BaseDirectory, "Data", "Downloads",
                                      GameId + "_loader.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        await File.WriteAllBytesAsync(archive, data, ct).ConfigureAwait(false);

        progress?.Report((95, $"Placing BepInEx into {gameFolder}…"));
        int placed = 0;
        using (var za = ZipFile.OpenRead(archive))
        {
            foreach (var e in za.Entries)
            {
                // ⚠⚠ Normalise the separator FIRST. A zip that stores its
                // paths with backslashes -- Azahar publishes one, and it broke
                // the 3DS install -- would fail every test below: the
                // directory check, the BepInEx/ prefix, all of it. Nothing
                // would be extracted and `placed` would stay 0, so the player
                // would be told BepInEx was installed while the folder stayed
                // empty. Silence is the worse half of this bug.
                string name = e.FullName.Replace('\\', '/');
                if (name.EndsWith('/') || e.Name.Length == 0) continue;
                // Only the loader's own tree — a zip with anything else in it
                // is not the release we asked for.
                bool ours = name.StartsWith("BepInEx/", StringComparison.Ordinal)
                         || name is "winhttp.dll" or "doorstop_config.ini"
                                  or ".doorstop_version" or "changelog.txt";
                if (!ours) continue;
                string dest = Path.Combine(gameFolder,
                    name.Replace('/', Path.DirectorySeparatorChar));
                // Never clobber: a player who already runs BepInEx keeps every
                // byte of their setup, and this pass only fills what is absent.
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest);
                placed++;
            }
        }
        // ⚠ No uninstall receipt on purpose: the loader is shared ground —
        // other mods the player installs later run through the same BepInEx,
        // so removing our game must not pull the floor out from under them.
        progress?.Report((100, placed > 0
            ? $"BepInEx placed — {placed} file(s). The game now loads the mod at start."
            : "BepInEx was already in the game folder — nothing touched."));
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

        if (Manifest.StandaloneGame)
        {
            // ⚠ PlanZip is deliberately skipped. It exists to keep London from
            // dropping files into a folder full of the PLAYER's game, and it
            // refuses layouts it does not recognise -- which is right for a
            // mod and wrong here, because this archive is not a mod. Ship of
            // Harkinian's top level is assets/, debug/, soh.exe: not a
            // mod-loader tree, and there is nothing to be careful around,
            // because the folder is London's own and holds only what London
            // put there. So it is extracted whole.
            progress?.Report((85, $"Unpacking the game into {gameFolder}…"));
            var placed = PcSetup.ExtractAll(archive, gameFolder, ct);
            PcSetup.WriteReceipt(ModReceiptPath, new PcSetup.Receipt(
                Manifest.ModUrl!, Manifest.Version, placed));

            string need = Manifest.BringYourOwn.Length > 0
                            && !HasBringYourOwn(gameFolder)
                ? " One thing left: open the setup check and press "
                  + $"\"Locate my {Manifest.BringYourOwn}…\"."
                : "";
            progress?.Report((100, doneLead + $"{DisplayName} is installed." + need));
            return;
        }

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

        if (Manifest.StandaloneGame)
        {
            // London provides this game, so there is no "your copy" to find.
            string folder = OwnGameFolder;
            string exe = Path.Combine(folder, Manifest.GameExe);
            bool here = Manifest.GameExe.Length > 0 && File.Exists(exe);
            list.Add(new GameComponent(
                "The game", here, ComponentNeed.Required,
                here ? $"{Manifest.GameExe} is in {folder}."
                     : "The game has not been downloaded yet.",
                here ? null : "Click Install — London downloads it from the "
                            + "author's release and unpacks it here."));

            if (Manifest.BringYourOwn.Length > 0)
            {
                bool got = HasBringYourOwn(folder);
                // ⚠ Optional on purpose. This is the ONE thing London cannot
                // do for the player, and it is also the one thing London
                // cannot verify -- the game checks the file against its own
                // hashes. A red light here would block the launch on a guess,
                // which is exactly the dead end this whole shape replaced.
                list.Add(new GameComponent(
                    $"Your own {Manifest.BringYourOwn}", got, ComponentNeed.Optional,
                    got ? $"Copied into {folder}."
                        : "The game cannot start without it.",
                    got ? null
                        : $"Press the \"Locate my {Manifest.BringYourOwn}…\" button "
                        + "in this window — London copies it into the game's "
                        + "folder. The game turns it into what it needs on first run.",
                    Manifest.ReleasePage.Length > 0 ? Manifest.ReleasePage : null));
            }
        }
        else if (kind is "apworld_and_mod" or "mod_package")
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
            if (LoaderDeclared) list.Add(LoaderComponent(folder));
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

    /// Has the player put their own file in the game's folder?
    ///
    /// Evidence only. The patterns come from the manifest, so this stays a
    /// property of the GAME rather than a list of ROM extensions baked into
    /// code shared by six hundred plugins.
    /// The same question, for the setup dialog. It lives in the same file and
    /// still asks through here, so there is one answer rather than two that
    /// can drift.
    internal bool HasOwnFile(string folder) => HasBringYourOwn(folder);

    /// The folder London manages for a game it provides. Exposed so the setup
    /// dialog opens THAT one and not a stale path in the settings.
    internal string ProvidedGameFolder => OwnGameFolder;

    private bool HasBringYourOwn(string folder)
    {
        if (Manifest.BringYourOwnFiles.Length == 0) return false;
        foreach (string pat in Manifest.BringYourOwnFiles.Split(
                     ',', StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (Directory.EnumerateFiles(folder, pat).Any()) return true;
            }
            catch { /* a folder that vanished is simply no evidence */ }
        }
        return false;
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

    /// Honest to the whole chain: mod files on disk mean nothing if the game
    /// never loads outside code. This row is what turned "everything is green
    /// but nothing works" into a fixable answer for TerraTech.
    private GameComponent LoaderComponent(string? gameFolder)
    {
        const string name = "Mod loader (BepInEx) in the game";
        if (gameFolder == null)
            return new GameComponent(name, false, ComponentNeed.Required,
                "Waiting for your game folder — the loader goes inside it.",
                "Locate the game folder first, then click Install.");
        return LoaderPresent(gameFolder)
            ? new GameComponent(name, true, ComponentNeed.Required,
                "BepInEx is in the game folder — the game loads the mod at start.")
            : new GameComponent(name, false, ComponentNeed.Required,
                "Without a loader the game starts clean and the mod's files are "
              + "never read — everything looks fine and nothing ever happens.",
                "Click Install — London fetches BepInEx from its own official "
              + "release and places it in the game folder.",
                "https://github.com/BepInEx/BepInEx/releases");
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

    // ------------------------------------------------------------ mod relay
    // Present only when the manifest declares one (client_protocol
    // "tcp_json"). This is the Diablo II pattern on a socket: London holds
    // the one AP connection and relays to the mod inside the game. The
    // protocol matches the world's own text client line for line, so a
    // player without London loses nothing — and a player with London never
    // sees a second client window.
    private ModRelay? _relay;
    private IApServices? _apServices;
    private GameWatcher? _watcher;
    private int _exitReported;   // GameExited is raised exactly once per session

    private void WireRelay()
    {
        if (!string.Equals(Manifest.ClientProtocol, "tcp_json", StringComparison.Ordinal))
            return;
        if (Manifest.ClientPort is <= 0 or > 65535)
            return;

        _relay = new ModRelay(Manifest.ClientPort,
                              msg => LogLine?.Invoke($"[{DisplayName}] {msg}"));
        _relay.ChecksResolved += ids => LocationsChecked?.Invoke(ids);
        _relay.GoalReached    += () => GoalCompleted?.Invoke();
        // ReportDeath checks DeathLinkEnabled itself; no second gate here.
        _relay.DeathReported  += cause => _apServices?.ReportDeath(cause);
    }

    public void OnApServicesAttached(IApServices? services) => _apServices = services;

    /// Who an item came from, in words a player recognises.
    ///
    /// Slot 0 is not a player: it is the server itself, which is what grants
    /// items released by another world, handed out by an admin, or included
    /// as starting inventory. Reporting that as "Player 0" is technically
    /// where the number came from and tells the player nothing.
    private string SenderName(int slot)
    {
        if (slot <= 0) return "Archipelago";
        string? name = _apServices?.ResolvePlayerName(slot);
        return string.IsNullOrWhiteSpace(name) ? $"Player {slot}" : name;
    }

    public void OnSlotData(JsonElement slotData) => _relay?.SetSlotData(slotData);

    public void OnItemTable(IReadOnlyDictionary<string, long> nameToId)
        => _relay?.SetItemTable(nameToId);

    public void OnLocationTable(IReadOnlyDictionary<string, long> nameToId)
    {
        _relay?.SetLocationTable(nameToId);
        _mcLocationTable = nameToId;
        McRefresh();
    }

    /// Checks landing — ours or replayed by the server. The board's live feed.
    public void OnCheckedLocations(long[] locationIds)
    {
        lock (_mcChecked)
            foreach (long id in locationIds) _mcChecked.Add(id);
        McRefresh();
    }

    private void McRefresh()
    {
        var w = _mcWindow;
        if (w == null) return;
        try { w.Dispatcher.BeginInvoke(w.Refresh); } catch (Exception) { }
    }

    // --- Mission Control window ---

    public bool SupportsSessionWindow
        => Manifest.SessionWindow.Length > 0 && _apServices?.SlotData != null;

    public void OpenSessionWindow()
    {
        var sd = _apServices?.SlotData;
        if (sd == null) return;
        if (_mcWindow is { IsLoaded: true })
        {
            _mcWindow.Activate();
            return;
        }
        HashSet<long> Snapshot()
        { lock (_mcChecked) return new HashSet<long>(_mcChecked); }

        _mcWindow = new Sc2MissionWindow(
            LastSlotName ?? "?", _mcServer ?? "session",
            () => (_apServices?.SlotData, _mcLocationTable, Snapshot()),
            () => _mcBridge,
            StartMissionBridge);
        _mcWindow.Closed += (_, _) => _mcWindow = null;
        _mcWindow.Show();
    }

    /// Start (or restart) the headless mission engine for the current session.
    private bool StartMissionBridge()
    {
        if (_mcAuth == null || _mcServer == null) return false;
        string? exe = Sc2Bridge.FindLauncherExe();
        if (exe == null)
        {
            LogLine?.Invoke($"[{DisplayName}] No Archipelago install found for the mission engine.");
            return false;
        }
        if (!Sc2Bridge.EnsureInstalled(exe, t => LogLine?.Invoke($"[{DisplayName}] {t}")))
            return false;

        _mcBridge ??= new Sc2Bridge();
        _mcBridge.LineReceived += line =>
        { if (line.StartsWith("LOG:")) LogLine?.Invoke($"[{DisplayName}] engine: {line[4..]}"); };
        _mcBridge.StateChanged += s =>
        {
            LogLine?.Invoke($"[{DisplayName}] mission engine: {s}");
            var w = _mcWindow;
            if (w != null)
                try { w.Dispatcher.BeginInvoke(() => w.SetFooter($"Engine: {s}")); }
                catch (Exception) { }
        };
        string? folder = RegisteredGameFolder;
        return _mcBridge.Start(exe, _mcAuth, _mcServer,
                               Manifest.LocatorKind.Length > 0 ? folder : null);
    }

    public Task OnDeathLinkReceivedAsync(string source, string cause)
    {
        _relay?.SendDeathLink(source, cause);
        return Task.CompletedTask;
    }

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
        // A fresh launch is a fresh session: whatever ended the last one must
        // not stop this one from reporting its own end.
        Interlocked.Exchange(ref _exitReported, 0);

        if (_relay != null)
        {
            // London is the AP client here; the mod dials our socket and the
            // player types nothing anywhere.
            _relay.Start();
            _relay.SetSession(GetOwnSlot?.Invoke() ?? -1, GetSeedName?.Invoke());
            LogLine?.Invoke($"[{DisplayName}] London relays Archipelago to the "
                          + "game's mod — start playing, everything else is wired.");
        }
        else
        {
            string where = session.ServerUri;
            LogLine?.Invoke($"[{DisplayName}] {DisplayName} connects to Archipelago from "
                          + $"inside the game. In its Archipelago screen, enter:");
            LogLine?.Invoke($"[{DisplayName}]   server: {where}");
            LogLine?.Invoke($"[{DisplayName}]   slot:   {session.SlotName}");
        }

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
                StartWatching();
            }
            else if (Manifest.StandaloneGame && Manifest.GameExe.Length > 0
                     && File.Exists(Path.Combine(OwnGameFolder, Manifest.GameExe)))
            {
                // London downloaded it and knows exactly where it is, so it
                // starts it. ⚠ WorkingDirectory matters: the game looks for
                // its own assets beside the exe, and a process started from
                // the launcher's folder would not find them.
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(OwnGameFolder, Manifest.GameExe),
                    WorkingDirectory = OwnGameFolder,
                    UseShellExecute = true,
                });
                IsRunning = true;
                LogLine?.Invoke($"[{DisplayName}] Started {Manifest.GameExe}.");
                StartWatching();
            }
            else if (Manifest.SessionWindow.Length > 0 && StartMissionControl(session))
            {
                // Mission Control takes the kivy client's place entirely:
                // London's own board window plus the headless engine. The
                // fallback below still exists — a machine where the bridge
                // cannot start keeps the world's own client.
                IsRunning = true;
                StartWatching();
            }
            else if (Manifest.WorldClientName.Length > 0 && StartWorldClient(session))
            {
                // The world's client IS the launcher for these games: it
                // connects to the multiworld and then starts the game with the
                // right map. Telling the player to start the game themselves
                // would be the wrong instruction, not just an unhelpful one --
                // a hand-started game is not in the session at all.
                IsRunning = true;
                StartWatching();
            }
            else
            {
                LogLine?.Invoke($"[{DisplayName}] Start the game yourself — London does "
                              + "not know where this one is installed.");
                // Not knowing how to START it does not mean we cannot SEE it:
                // if the folder is known, the watcher still notices the game
                // appear and, later, close.
                IsRunning = true;
                StartWatching();
            }
        }
        catch (Exception e)
        {
            LogLine?.Invoke($"[{DisplayName}] Could not start it: {e.Message}");
        }

        LastSlotName = session.SlotName;
        return Task.CompletedTask;
    }

    /// London's takeover of a world-client game: remember the session's
    /// endpoint, start the headless engine, open the board. Returns false so
    /// the kivy fallback can run when any of it cannot.
    private bool StartMissionControl(ApSession session)
    {
        try
        {
            _mcServer = session.ServerUri
                .Replace("ws://", "").Replace("wss://", "").TrimEnd('/');
            _mcAuth = Uri.EscapeDataString(session.SlotName) + ":"
                    + Uri.EscapeDataString(session.Password ?? "");
            LastSlotName = session.SlotName;

            if (!StartMissionBridge()) return false;

            var app = System.Windows.Application.Current;
            if (app != null)
                app.Dispatcher.BeginInvoke(OpenSessionWindow);
            LogLine?.Invoke($"[{DisplayName}] Mission Control is open — pick a "
                          + "mission there and London launches it.");
            return true;
        }
        catch (Exception e)
        {
            LogLine?.Invoke($"[{DisplayName}] Mission Control failed ({e.Message}) — "
                          + "falling back to the game's own client.");
            return false;
        }
    }

    /// Start the client the WORLD ships, and hand it this session.
    ///
    /// Same contract as EmulatorPlugin.StartWorldClient, for PC games that
    /// have no emulator: London opens nothing on the session itself, because
    /// a second reader on one slot is a bug, not redundancy. The world's
    /// client connects, and it is what starts the game.
    ///
    /// ⚠ The slot rides INSIDE the connect URI. Archipelago's client parser
    /// has only --connect and --password; an unknown --name kills the whole
    /// parse and the client sits mute. The colon is always there because the
    /// websockets library refuses a username without a password.
    private bool StartWorldClient(ApSession session)
    {
        try
        {
            var st = SettingsStore.Load();
            string? exe = null;
            foreach (string root in new[] { st.ApEnginePath, st.ApworldSyncDir,
                                            @"C:\ProgramData\Archipelago" })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string cand = Path.Combine(root, "ArchipelagoLauncher.exe");
                if (File.Exists(cand)) { exe = cand; break; }
            }
            if (exe == null)
            {
                LogLine?.Invoke($"[{DisplayName}] Could not start "
                    + $"{Manifest.WorldClientName}: no Archipelago install found.");
                return false;
            }

            string server = session.ServerUri
                .Replace("ws://", "").Replace("wss://", "").TrimEnd('/');
            string auth = Uri.EscapeDataString(session.SlotName) + ":"
                        + Uri.EscapeDataString(session.Password ?? "");

            // ⚠⚠ EXACTLY the form the emulator path uses, scheme and all.
            // A bare "slot:pw@host:port" is not what the client's parser
            // expects -- the proven call is archipelago://…, and this was
            // written without it once and the client simply did not connect.
            //
            // ⚠⚠ NO --nogui. Archipelago's clients are kivy apps: started
            // without a window they come up half-initialised and die on the
            // first message the server sends. A visible client window is a far
            // smaller price than a session that quietly never joins.
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                Arguments        = $"\"{Manifest.WorldClientName}\" "
                                 + $"-- --connect \"archipelago://{auth}@{server}\"",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };

            // ⚠ The client finds the game through this. Its own detection reads
            // Documents\StarCraft II\ExecuteInfo.txt, which is right until the
            // player has more than one install or the file is stale -- London
            // already knows which folder it installed the data into, so it
            // says so rather than letting the two disagree.
            string? folder = RegisteredGameFolder;
            if (folder != null && Manifest.LocatorKind.Length > 0)
                psi.EnvironmentVariables["SC2PATH"] = folder;

            Process.Start(psi);
            LogLine?.Invoke($"[{DisplayName}] Started {Manifest.WorldClientName} and "
                          + "pointed it at your session — it opens the game itself.");
            return true;
        }
        catch (Exception e)
        {
            LogLine?.Invoke($"[{DisplayName}] Could not start "
                          + $"{Manifest.WorldClientName}: {e.Message}");
            return false;
        }
    }

    /// Watch the game's own process, since Steam hands us nothing to hold.
    /// Silent when the game folder is unknown — the setup check already says
    /// so, and a second complaint at launch helps nobody.
    private void StartWatching()
    {
        string? folder = RegisteredGameFolder;
        if (folder == null) return;

        _watcher?.Stop();
        _watcher = new GameWatcher(folder,
            msg => LogLine?.Invoke($"[{DisplayName}] {msg}"),
            code => ReportExit(code));
        _watcher.Start();
    }

    /// One exit per session, wherever it comes from — the watcher seeing the
    /// process vanish, or the player pressing Stop. Two would end the session
    /// twice and, on the join path, log a second "Game closed" over a session
    /// that is already gone.
    private void ReportExit(int code)
    {
        if (Interlocked.Exchange(ref _exitReported, 1) != 0) return;
        IsRunning = false;
        _relay?.Stop();          // free the port for the next session
        _watcher?.Stop();
        GameExited?.Invoke(code);
    }

    public Task StopAsync()
    {
        // London did not take the game over, so it does not get to close it.
        // Saying "stopped" would only be true of our own bookkeeping.
        ReportExit(0);
        return Task.CompletedTask;
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        _relay?.PutItems(items, index, SenderName);
        return Task.CompletedTask;
    }

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

    /// Download straight to disk, reporting percent as it goes.
    ///
    /// ⚠ Not DownloadAsync with a bigger buffer: a data package is hundreds of
    /// megabytes, and holding one as a byte[] spikes the launcher's memory for
    /// no reason. No overall timeout either -- a five-minute cap turns a slow
    /// connection into a failed install; the cancellation token is what stops
    /// this, and the player owns that.
    public static async Task DownloadToFileAsync(string url, string path,
                                                 Action<int>? onPercent,
                                                 CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        int lastPct = -1;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total is > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                if (pct != lastPct) { lastPct = pct; onPercent?.Invoke(pct); }
            }
        }
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
    /// A file-dialog filter from the manifest's own patterns.
    ///
    /// "*.z64,*.n64,*.v64,oot.o2r" becomes one entry naming them all plus an
    /// "All files" escape hatch — a player whose dump has an odd extension
    /// must not be locked out by our list.
    public static string FilterFor(string patterns, string label)
    {
        var pats = patterns.Split(',', StringSplitOptions.RemoveEmptyEntries
                                     | StringSplitOptions.TrimEntries)
                           .Where(p => p.Length > 0).ToArray();
        if (pats.Length == 0) return "All files|*.*";
        return $"{label} ({string.Join("; ", pats)})|{string.Join(";", pats)}"
             + "|All files|*.*";
    }

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
                // ⚠ ONE shared word is not evidence. This used to accept any
                // token of four letters or more, so a Majora's Mask ROM in the
                // folder was shown as proof that it was the Ocarina of Time
                // folder -- "zelda" matched, and the setup check went green on
                // the wrong game. Either two distinct words of the title, or
                // one word long enough to be the title's own ("harkinian",
                // "blasphemous"), never a word every game in the series shares.
                int hits = tokens.Count(name.Contains);
                if (hits >= 2 || tokens.Any(t => t.Length >= 8 && name.Contains(t)))
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

    /// Unpack a whole archive into a folder London owns.
    ///
    /// For a standalone game the archive IS the game, so there is no layout to
    /// recognise and nothing of the player's to be careful around — the folder
    /// holds only what London put there. PlanZip's caution is right for a mod
    /// dropped into somebody's Steam install and wrong here.
    ///
    /// ⚠ Entry names are still checked. A zip may name an entry "..\..\x",
    /// and extracting that writes outside the folder we were handed.
    public static List<ReceiptFile> ExtractAll(string archivePath, string destFolder,
                                               CancellationToken ct)
    {
        var placed = new List<ReceiptFile>();
        string root = Path.GetFullPath(destFolder);
        Directory.CreateDirectory(root);

        using var za = ZipFile.OpenRead(archivePath);
        foreach (var entry in za.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith("/") || entry.Length == 0
                && entry.Name.Length == 0) continue;          // a directory

            string dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase)
                && !dest.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The archive tries to write outside the game folder "
                  + $"(\"{entry.FullName}\"). Nothing was installed.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string part = dest + ".part";
            entry.ExtractToFile(part, overwrite: true);
            File.Move(part, dest, overwrite: true);
            placed.Add(new ReceiptFile(
                Path.GetRelativePath(root, dest), entry.Length));
        }
        return placed;
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

            // — The folder picker is for a game the PLAYER owns. London
            // provides a standalone one, so offering "Change game folder"
            // there invites exactly the mistake this shape removed: pointing
            // at another copy on disk that London does not manage.
            bool folderKind = !manifest.StandaloneGame
                              && manifest.Install is "apworld_and_mod" or "mod_package";
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

            // — The one thing London cannot download. Same shape as every
            // ROM game in the catalogue: ask where the file is, then COPY it
            // into the install folder. Telling the player to go and put it
            // there themselves is a step we can simply take for them.
            if (manifest.StandaloneGame && manifest.BringYourOwn.Length > 0
                && gameFolder != null && !plugin.HasOwnFile(gameFolder))
                AddButton($"Locate my {manifest.BringYourOwn}…",
                    "Point London at the file. It is copied into the game's folder; "
                  + "your original is left where it is.",
                    () =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = $"Locate your {manifest.BringYourOwn}",
                            Filter = PcSetup.FilterFor(manifest.BringYourOwnFiles,
                                                       manifest.BringYourOwn),
                            CheckFileExists = true,
                        };
                        if (dlg.ShowDialog(win) != true) return;
                        string dest = Path.Combine(gameFolder!,
                                                   Path.GetFileName(dlg.FileName));
                        // Copy, never move: it is the player's file and it may
                        // well be the copy another game of theirs uses.
                        File.Copy(dlg.FileName, dest, overwrite: true);
                        status.Text = $"Copied {Path.GetFileName(dest)} into the game folder.";
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

            if (!manifest.StandaloneGame
                && downloadedMod.Length > 0 && File.Exists(downloadedMod))
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
            // — For a game London provides, the settings entry is not the
            // answer and may be a leftover from before this game had a shape
            // of its own. Ship of Harkinian still had C:\spil\... registered
            // from an earlier attempt, so "Open game folder" opened the
            // player's old download instead of the one London installed.
            if (manifest.StandaloneGame) return plugin.ProvidedGameFolder;

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
    string Steps,
    string ClientProtocol,
    int ClientPort,
    string LoaderKind,
    string? LoaderUrl,
    // --- a game London provides itself -----------------------------------
    //
    // A standalone fan port is NOT a mod for a game you own: the release's
    // zip IS the game. Ship of Harkinian shipped as "apworld_and_mod", so the
    // setup check demanded "Your copy of the game" -- a folder for something
    // that exists nowhere, is not on Steam, and that London could simply have
    // downloaded. The player could not get past it.
    bool StandaloneGame,
    // The executable inside that download, so London can start it. Without a
    // Steam id there is nothing else to go on.
    string GameExe,
    // What the player must still put in the folder, or empty. ⚠ Not every
    // standalone port is self-contained: Ship of Harkinian ships no
    // copyrighted assets and turns the player's own ROM into what it needs on
    // first run. Saying "nothing needed" there would be a lie the player only
    // discovers when the game refuses to start.
    string BringYourOwn,
    // The patterns that show the player has done it -- "*.z64,*.n64,oot.o2r".
    // ⚠ Evidence, not validation: Ship of Harkinian checks the ROM against
    // its own hash list, and London has no business second-guessing that. A
    // match turns the reminder green; a miss leaves it amber and never blocks.
    string BringYourOwnFiles,

    // --- a game whose world ships its own client -------------------------
    //
    // The name the world registers its client under in Archipelago's Launcher
    // ("Starcraft 2 Client"). Empty for every game where London or a bridge is
    // the client. When set, Play starts THAT client and hands it the session,
    // instead of telling the player to start the game themselves -- which for
    // these games is not even the right instruction, because the world's
    // client is what launches the game.
    //
    // ⚠ Only set this when the world's launch_client takes *args. One that
    // declares launch_client() dies on the arguments and nothing reaches the
    // server -- see EmulatorPlugin.StartWorldClient for the measurement.
    string WorldClientName,

    // --- data the game needs beside itself -------------------------------
    //
    // Some worlds need a package of maps/mods dropped into the GAME's folder
    // before anything works, and ship it as a GitHub release rather than in
    // the apworld. Starcraft 2 is the first: eleven .SC2Mod folders and 83
    // campaign maps, 383 MB, which its client otherwise makes the player fetch
    // by typing /download_data into a console.
    //
    // "owner/repo", the release TAG (these are pinned to an API version, not
    // "latest" -- a newer tag belongs to a newer apworld), and the asset name.
    string DataRepo,
    string DataTag,
    string DataAsset,
    // One path inside the game folder that proves the package is unpacked, so
    // Install can be honest about whether there is anything left to do.
    string DataMarker,

    // The receipt file the world's client checks the package version with
    // (SC2: ArchipelagoSC2Metadata.txt). Empty = the world keeps no receipt.
    string DataMetadataFile,

    // Non-empty = this game's session gets London's own window (a mission
    // board) instead of the world's kivy client. Value names the board kind;
    // "sc2_missions" is the first.
    string SessionWindow,

    // How to find the game when Steam and the usual roots cannot: a named,
    // verifiable lookup rather than a guess. "sc2_executeinfo" reads the path
    // out of Documents\StarCraft II\ExecuteInfo.txt, which is the same file
    // the world's own client reads.
    string LocatorKind)
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
            S(r, "steps"),
            S(r, "client_protocol"),
            r.TryGetProperty("client_port", out var cp)
                && cp.ValueKind == JsonValueKind.Number ? cp.GetInt32() : 0,
            S(r, "loader_kind"),
            N(r, "loader_url"),
            r.TryGetProperty("standalone_game", out var sg)
                && sg.ValueKind == JsonValueKind.True,
            S(r, "game_exe"),
            S(r, "bring_your_own"),
            S(r, "bring_your_own_files"),
            S(r, "world_client_name"),
            S(r, "data_repo"),
            S(r, "data_tag"),
            S(r, "data_asset"),
            S(r, "data_marker"),
            S(r, "data_metadata_file"),
            S(r, "session_window"),
            S(r, "locator_kind"));
    }
}
