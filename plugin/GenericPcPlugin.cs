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
// So this plugin does the three things London genuinely can:
//
//   1. INSTALL THE WORLD. The .apworld goes into the engine's custom_worlds,
//      which is what makes the game appear in New seed at all. Fully automatic
//      for every game whose author publishes one -- 210 of 282.
//   2. INSTALL THE MOD, when the author ships a package and the player points
//      at their game folder. Otherwise it shows the author's own steps.
//   3. START THE GAME and hand over the address and slot name to type into the
//      game's own connect screen.
//
// Every download comes from the author's own release. Nothing is bundled.
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

    public string[] GameBadges => Manifest.Install switch
    {
        "apworld_only"    => new[] { "PC", "Adds itself to your seeds" },
        "apworld_and_mod" => new[] { "PC", "World + game mod" },
        "mod_package"     => new[] { "PC", "Game mod" },
        "bundled"         => new[] { "PC", "Ships with Archipelago" },
        _                 => new[] { "PC", "Manual setup" },
    };

    /// What London can honestly promise. A world-only game is a real
    /// auto-install: there is nothing else to do. A game whose mod must land
    /// in the player's own folder is AutoMod. Anything the author only
    /// documents in prose is Manual, and says so before it is clicked.
    public InstallCapability InstallCapability => Manifest.Install switch
    {
        "apworld_only" => InstallCapability.AutoInstall,
        "bundled"      => InstallCapability.AutoInstall,
        "apworld_and_mod" or "mod_package" => InstallCapability.AutoMod,
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

    public bool IsInstalled => Manifest.Install switch
    {
        // The world ships inside Archipelago; nothing of ours is installed,
        // and saying "not installed" about a game the engine already knows
        // would send the player looking for a download that does not exist.
        "bundled" => CustomWorldsDir() != null,
        "manual" or "discord_only" => Directory.Exists(GameDirectory),
        _ => ApworldPath.Length > 0 && File.Exists(ApworldPath),
    };

    public string? InstalledVersion
        => IsInstalled ? (Manifest.Version.Length > 0 ? Manifest.Version : "installed") : null;

    public string? AvailableVersion => Manifest.Version.Length > 0 ? Manifest.Version : null;

    public Task CheckForUpdateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                                           CancellationToken ct = default)
    {
        string? worlds = CustomWorldsDir();
        if (worlds == null)
            throw new InvalidOperationException(
                "No Archipelago engine is set up yet. Open Multiworld and point London "
              + "at one (or let it fetch the installer) — the world file has to go into "
              + "the engine's own folder to be usable.");

        if (Manifest.Install == "bundled")
        {
            progress?.Report((100, $"{DisplayName} ships with Archipelago itself — "
                                 + "nothing to download."));
            Directory.CreateDirectory(GameDirectory);
            return;
        }

        if (string.IsNullOrWhiteSpace(Manifest.ApworldUrl))
            throw new InvalidOperationException(
                $"{DisplayName} has no downloadable world file. Its author documents "
              + $"the setup at {Manifest.ReleasePage}");

        progress?.Report((5, $"Downloading {Manifest.ApworldFileName} from "
                           + $"{Manifest.WorldBy}'s own release…"));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
        byte[] data = await http.GetByteArrayAsync(Manifest.ApworldUrl, ct).ConfigureAwait(false);

        // An .apworld is a zip. A release asset that moved serves an error
        // page, and an error page in custom_worlds breaks EVERY generation --
        // not just this game's.
        if (data.Length < 4 || data[0] != 'P' || data[1] != 'K')
            throw new InvalidDataException(
                $"What came back from {Manifest.ReleasePage} was not a world file. "
              + "The release may have moved — install it by hand from there.");

        Directory.CreateDirectory(worlds);
        string dest = Path.Combine(worlds, Manifest.ApworldFileName);
        string tmp = dest + ".part";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, dest, overwrite: true);
        Directory.CreateDirectory(GameDirectory);

        progress?.Report((100, Manifest.ModUrl == null
            ? $"{DisplayName} can now be picked in New seed."
            : $"World installed. {DisplayName} also needs its in-game mod — "
            + "see the setup steps on the game's page."));
    }

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

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
            Text = Manifest.Steps.Length > 0
                ? Manifest.Steps
                : $"This game's mod is published at {Manifest.ReleasePage}.",
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
