using System.IO;
using System.Linq;
using System.Text.Json;

using LauncherV2.Core;
using LauncherV2.Core.Patching;
using LauncherV2.Core.Plugins;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Catalog;

// One plugin, many games. The behaviour comes from an embedded game.json,
// so a new game is a data file -- not a new class.
//
// The manifest must GUESS at nothing. Where a field is empty, the plugin says
// so to the player instead of inventing a value. See catalog/SCHEMA.md.
public class GenericEmulatorPlugin : EmulatorPlugin
{
    protected readonly GameManifest Manifest;

    // The manifest lives INSIDE the assembly, not beside it. pack_plugin.py
    // deliberately whitelists only assembly + deps + plugin.json, so a loose
    // game.json would never reach the package and the plugin would start
    // unable to find itself. As an embedded resource the two cannot be parted.
    public GenericEmulatorPlugin()
    {
        var asm = GetType().Assembly;
        string? name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("game.json",
                                               StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException(
                $"{asm.GetName().Name} has no embedded game.json. The plugin was "
              + "built without its manifest - see catalog/SCHEMA.md.");

        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        Manifest = GameManifest.Parse(r.ReadToEnd());
    }

    public override string GameId      => Manifest.Id;
    public override string DisplayName => Manifest.DisplayName;
    public override string Subtitle    => Manifest.Subtitle;
    public override string ApWorldName => Manifest.ApWorldName;
    public override string Description => Manifest.Description;

    protected override string RomSystem     => Manifest.Platform;
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";

    /// Which file under Plugins/Scripts/games/ carries this game's RAM map.
    ///
    /// NOT the game id. The modules were written before this catalogue existed
    /// and use their own short names -- "cvcotm" for castlevania_cotm, "mm2"
    /// for mega_man_2, "The Minish Cap" with spaces. Letting the id decide
    /// would silently load nothing for ten of the sixteen GBA games.
    protected override string LuaModuleName => Manifest.LuaModule ?? Manifest.Id;

    /// Only becomes true once the RAM map for THIS game has been measured
    /// in-game. Until then the launcher warns at launch, so nobody sits in a
    /// multiworld for an hour with no checks arriving.
    public override bool ChecksImplemented => Manifest.ChecksVerified;

    /// One entry per accepted dump. A game can legitimately accept more than
    /// one -- the Castlevania worlds take the original cartridge dump AND the
    /// Advance Collection rip, which are different files.
    ///
    /// Size is 0 when we have not measured it. The launcher reads that as
    /// "size unknown" and matches on MD5 alone, which is the stronger check.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => Manifest.Rom is null
            ? Array.Empty<RomIdentity>()
            : Manifest.Rom.Md5.Count > 0
                ? Manifest.Rom.Md5
                          .Select(h => new RomIdentity(Manifest.Rom.Size, h,
                                                       Manifest.Rom.Description))
                          .ToArray()
                : Manifest.Rom.Size > 0
                    ? new[] { new RomIdentity(Manifest.Rom.Size, null,
                                              Manifest.Rom.Description) }
                    : Array.Empty<RomIdentity>();

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;

        string what = Manifest.Rom?.Description ?? $"a {RomSystem} {DisplayName} ROM";
        var hashes = Manifest.Rom?.Md5 ?? (IReadOnlyList<string>)Array.Empty<string>();
        if (hashes.Count == 0)
            what += "  —  this edition cannot be checked exactly; "
                  + "make sure yourself that it is the right dump";
        else if (hashes.Count > 1)
            what += $"  —  {hashes.Count} different dumps are accepted";

        // RomRequirement shows ONE hash. With several accepted dumps there is no
        // single right answer, so it shows none rather than naming one and
        // making the other look wrong; AcceptableBaseRoms still checks them all.
        return new RomRequirement(DisplayName, RomSystem, what,
                                  hashes.Count == 1 ? hashes[0] : null,
                                  WrongVersionPresent: false, BuildRomFilter());
    }
    /// Turn the player's own ROM into THIS seed's ROM.
    ///
    /// Without this the launcher starts the plain library ROM: the emulator
    /// runs, the connector attaches, and the game sits there being ordinary
    /// vanilla. Nothing errors -- there are simply never any checks. So the
    /// note below is written on every path, including the ones that do nothing.
    ///
    /// The patch says which slot it belongs to, so nothing has to be configured:
    /// a player drops all their patches in and each seed picks its own.
    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        await Task.CompletedTask;

        string? patch = ResolvePatch(session.SlotName, out string note);
        if (patch is null)
        {
            SessionRomNote = note;
            return null;
        }

        // One output per slot, rebuilt every launch. Patching a 16 MB ROM takes
        // a fraction of a second, and always rebuilding means a re-generated
        // seed can never be played against yesterday's patched copy.
        string safeSlot = string.Concat(session.SlotName
                            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string ending = ApPatch.ReadManifest(patch)?.ResultFileEnding ?? ".rom";
        string outPath = Path.Combine(RomLibraryDirectory, "sessions", safeSlot + ending);

        var result = ApPatch.Apply(patch, RomPath!, outPath);
        SessionRomNote = $"[{DisplayName}] Patched for slot \"{session.SlotName}\" "
                       + $"({result.Size / (1024 * 1024)} MB, MD5 {result.Md5[..8]}).";
        return outPath;
    }

    /// Which patch file is this (seed, slot)'s, and why not, when there is none.
    ///
    /// Two ways in, in this order:
    ///   1. The store, keyed by (seed, slot). This is the real answer -- it was
    ///      recorded when the player handed the patch over while connected to
    ///      that seed, which is the only moment the link is known.
    ///   2. Any patch in the folder whose manifest names this slot and game.
    ///      Covers a file dropped on the window before ever connecting, and the
    ///      patches already sitting in folders from before the store existed.
    ///      Weaker: a second seed for the same slot name would also match, which
    ///      is exactly why (1) is tried first and why (2) records what it finds.
    string? ResolvePatch(string slot, out string note)
    {
        note = "";
        string? seed = GetSeedName?.Invoke();
        var store = SeedPatchStore.For(GameId);

        if (!string.IsNullOrWhiteSpace(seed))
        {
            string? known = store.Resolve(seed!, slot);
            if (known != null) return known;
        }

        string patchDir = store.PatchDirectory;
        if (Directory.Exists(patchDir))
        {
            var loose = Directory.EnumerateFiles(patchDir)
                .Select(p => (Path: p, M: ApPatch.ReadManifest(p)))
                .Where(c => c.M is not null
                         && string.Equals(c.M!.Game, ApWorldName, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(c.M!.PlayerName, slot, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Path)
                .FirstOrDefault();

            if (loose != null)
            {
                // Claim it for this seed so the guess only ever happens once.
                if (!string.IsNullOrWhiteSpace(seed))
                    try { return store.Import(loose, seed!, slot, ApWorldName); }
                    catch { /* keep playing with the file we found */ }
                return loose;
            }
        }

        note = $"[{DisplayName}] No patch stored for slot \"{slot}\""
             + (string.IsNullOrWhiteSpace(seed) ? "" : $" in seed {seed}")
             + " — playing the unpatched ROM, so no checks will be sent.";
        return null;
    }

    public SeedPatchRequest? GetUnmetSeedPatch(string seed, string slot)
    {
        // Asked BEFORE launch, when the seed is known but the plugin has not run
        // yet, so this must not depend on GetSeedName having been called.
        if (SeedPatchStore.For(GameId).Resolve(seed, slot) != null) return null;

        return new SeedPatchRequest(DisplayName, seed, slot,
            "Archipelago patch (*.ap*)|*.ap*|All files (*.*)|*.*");
    }

    public string? ImportSeedPatch(string sourcePath, string seed, string slot)
    {
        try
        {
            SeedPatchStore.For(GameId).Import(sourcePath, seed, slot, ApWorldName);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // GameBadges is inherited too -- the base "ROM needed" is exactly right here.
}

/// Md5 is a LIST: some games accept more than one legitimate dump. Empty means
/// no hash is known, which is a real state -- not an error to paper over.
public sealed record RomSpecManifest(
    string Description, long Size, IReadOnlyList<string> Md5);

public sealed record GameManifest(
    string Id, string DisplayName, string Subtitle, string Platform,
    string ApWorldName, string Description,
    RomSpecManifest? Rom, bool ChecksVerified, string? LuaModule)
{
    public static GameManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;

        RomSpecManifest? rom = null;
        if (r.TryGetProperty("rom", out var ro) && ro.ValueKind == JsonValueKind.Object)
        {
            long size = ro.TryGetProperty("size", out var s)
                     && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;

            // md5 accepts a single string or an array, so a one-dump game does
            // not have to write a one-element list.
            var md5 = new List<string>();
            if (ro.TryGetProperty("md5", out var m))
            {
                if (m.ValueKind == JsonValueKind.String)
                    md5.Add(m.GetString()!);
                else if (m.ValueKind == JsonValueKind.Array)
                    md5.AddRange(m.EnumerateArray()
                                  .Where(e => e.ValueKind == JsonValueKind.String)
                                  .Select(e => e.GetString()!));
            }

            rom = new RomSpecManifest(
                Str(ro, "description") ?? "your own copy of the game", size, md5);
        }

        return new GameManifest(
            Str(r, "id")!, Str(r, "display_name")!,
            Str(r, "subtitle") ?? "",
            Str(r, "platform") ?? "GBA",
            Str(r, "ap_world_name") ?? Str(r, "display_name")!,
            Str(r, "description") ?? "",
            rom,
            r.TryGetProperty("checks_verified", out var c)
                && c.ValueKind == JsonValueKind.True,
            Str(r, "lua_module"));
    }

    static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
}
