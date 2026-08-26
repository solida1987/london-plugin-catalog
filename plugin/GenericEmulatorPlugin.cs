using System.IO;
using System.IO.Compression;
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

    /// Which bridge extension carries this game's checks, straight from the
    /// manifest. BizHawk is the default only because most of the catalogue is
    /// BizHawk -- a SNES game says "sni" and the launcher resolves that in
    /// BridgeRegistry, with no code here knowing what SNI is.
    protected override string ClientProtocol => Manifest.ClientProtocol;

    /// client.kind == "world": the world ships its own Archipelago client, so
    /// London starts the emulator and stands aside — no Lua map, no memory
    /// reading, no slot connection. The manifest may only say "world" with a
    /// quoted source (the build gate enforces that), so this override is as
    /// audited as the field it reads. First user: Burnout 3 over PINE.
    protected override bool WorldCarriesOwnClient
        => string.Equals(Manifest.ClientKind, "world", StringComparison.OrdinalIgnoreCase);

    protected override string? WorldClientName => Manifest.ClientName;

    /// Who made each part, shown on the game's page.
    ///
    /// None of this is our game. A catalogue plugin is an installer: it points
    /// the launcher at a game the player already owns and at an Archipelago
    /// world somebody else wrote. Both of those people get named here, above
    /// the line that says what is actually ours -- and a credit we cannot back
    /// up is left out rather than guessed at.
    public override IReadOnlyList<GameCredit> Credits
    {
        get
        {
            var c = Manifest.Credits;
            var list = new List<GameCredit>();

            if (!string.IsNullOrWhiteSpace(c?.GameBy))
                list.Add(new GameCredit("Game by", c!.GameBy!, Highlight: true));
            else
                // Say it plainly even when the manifest cannot name them: the
                // game is somebody else's, and the player brings their own copy.
                list.Add(new GameCredit("The game",
                    "belongs to its publisher -- you supply your own copy",
                    Highlight: true));

            // Name the people first. The organisation is who holds the licence,
            // not who did the work, and a world's authors deserve to see their
            // own names where a player looks.
            if (c is not null && c.WorldAuthors.Count > 0)
            {
                list.Add(new GameCredit("Archipelago world by",
                                        string.Join(", ", c.WorldAuthors),
                                        Highlight: true));
                if (!string.IsNullOrWhiteSpace(c.WorldBy))
                    list.Add(new GameCredit("Released by", c.WorldBy!));
            }
            else if (!string.IsNullOrWhiteSpace(c?.WorldBy))
                list.Add(new GameCredit("Archipelago world by", c!.WorldBy!,
                                        Highlight: true));

            if (c is not null && c.SetupGuideBy.Count > 0)
                list.Add(new GameCredit("Setup guide by",
                                        string.Join(", ", c.SetupGuideBy)));

            list.Add(new GameCredit("Launcher plugin by",
                                    c?.PluginBy ?? "solida1987"));
            return list;
        }
    }

    /// The in-emulator connector. Only meaningful for the Lua protocols: a
    /// bridge that attaches to a running program (SNI) never reads it, and
    /// BridgeContext.ScriptPath is simply ignored on that path.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";

    /// Which file under Plugins/Scripts/games/ carries this game's RAM map.
    ///
    /// NOT the game id. The modules were written before this catalogue existed
    /// and use their own short names -- "cvcotm" for castlevania_cotm, "mm2"
    /// for mega_man_2, "The Minish Cap" with spaces. Letting the id decide
    /// would silently load nothing for ten of the sixteen GBA games.
    protected override string LuaModuleName => Manifest.LuaModule ?? Manifest.Id;

    /// The world's OWN connector filename, when it ships one. London looks for
    /// it under Archipelago's data/lua/ -- which is where the world's client
    /// puts it -- and hands it to the emulator.
    protected override string? WorldConnectorLuaName => Manifest.WorldConnectorLua;

    protected override string? WorldRomPathSetting => Manifest.WorldRomPathSetting;

    protected override string? WorldRelayName => Manifest.WorldRelay;

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
    /// What this seed's ROM is called on disk.
    ///
    /// ⚠ THE SEED BELONGS IN THE NAME, not only in the bytes. Emulators name
    /// the battery save after the ROM file, so two seeds played under the same
    /// slot name shared one save: a brand new multiworld opened on the previous
    /// one's file-select screen with its characters still on it. The ROM was
    /// rebuilt correctly on every launch -- the save was not, and a save that
    /// outlives its seed is the one thing a multiworld cannot have.
    ///
    /// The single top-level 16-hex-digit folder of a 3DS mod zip, or null.
    /// Public and static so the rule can be checked without a running game.
    public static string? ThreeDsModTitleId(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var tops = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tops.Count != 1) return null;
            string t = tops[0];
            return t.Length == 16 && t.All(Uri.IsHexDigit) ? t : null;
        }
        catch { return null; }
    }

    /// Put THIS seed's mod in the emulator's mods folder, and launch a
    /// per-seed copy of the plain ROM.
    ///
    /// The mods folder is global to the emulator, so it is owned by whichever
    /// seed launched last: the title folder is deleted and rewritten whenever
    /// the marker disagrees. A stale seed playing under a new one is the same
    /// leak as replayed checks, and it is closed the same way -- by force.
    private string? InstallThreeDsModSession(string zip, string titleId,
                                             ApSession session)
    {
        string seed = GetSeedName?.Invoke() ?? "";
        string stamp = seed + "|" + session.SlotName;
        string modsRoot = Path.Combine(EmulatorDirectory, "user", "load", "mods");
        string target = Path.Combine(modsRoot, titleId);
        string marker = Path.Combine(target, ".london_seed");

        bool current = File.Exists(marker)
                    && File.ReadAllText(marker).Trim() == stamp;
        if (!current)
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(modsRoot);
            ZipFile.ExtractToDirectory(zip, modsRoot, overwriteFiles: true);
            File.WriteAllText(marker, stamp);
        }

        string? rom = CopyForSaveIsolation(session);
        SessionRomNote = $"[{DisplayName}] Seed mod installed for slot "
                       + $"\"{session.SlotName}\"; playing a per-seed copy of "
                       + "your own dump with it.";
        return rom;
    }

    /// A per-(slot, seed) copy of the library ROM, or null when the seed is
    /// not known -- best effort, because the game must still start.

    /// Build this seed's ROM by applying the world's own fixed patch.
    ///
    /// The patch and the md5 to check against both come out of the apworld
    /// the player installed, so London produces the same bytes the world's
    /// own client would -- and proves it did before handing the file over.
    private string? ApplyStaticPatchForSession(StaticPatchManifest sp, ApSession session)
    {
        string? seed = GetSeedName?.Invoke();
        if (RomPath is null || !File.Exists(RomPath) || string.IsNullOrWhiteSpace(seed))
            return null;

        string outPath;
        try
        {
            outPath = Path.Combine(RomLibraryDirectory, "sessions",
                SessionRomName(session.SlotName, seed!, Path.GetExtension(RomPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        }
        catch { return null; }

        // ⚠ An existing session file is NOT proof it is the right one: the
        // build that wrote it may be the one that copied the retail ROM.
        // Hash it, and rebuild when it is wrong.
        if (File.Exists(outPath) && ComputeMd5(outPath) == sp.ResultMd5)
        {
            SessionRomNote = $"[{DisplayName}] Playing this seed's patched ROM.";
            return outPath;
        }

        byte[] rom;
        try { rom = File.ReadAllBytes(RomPath); } catch { return null; }

        // The player may have brought a ROM the world already patched.
        if (Md5Hex(rom) == sp.ResultMd5)
        {
            File.WriteAllBytes(outPath, rom);
            SessionRomNote = $"[{DisplayName}] Your ROM is already patched for "
                           + "this game; playing a copy named for this seed.";
            return outPath;
        }

        // N64 dumps come in both byte orders and the patch expects one of them.
        if (Manifest.Rom!.ByteswapMd5.Contains(Md5Hex(rom)))
        {
            var sw = new byte[rom.Length];
            for (int i = 0; i + 1 < rom.Length; i += 2) { sw[i] = rom[i + 1]; sw[i + 1] = rom[i]; }
            if ((rom.Length & 1) == 1) sw[rom.Length - 1] = rom[rom.Length - 1];
            rom = sw;
        }

        byte[]? patch = ReadFromApworld(sp.Entry);
        if (patch is null)
        {
            // Say the true thing rather than launching something that cannot work.
            throw new SessionRomRefusedException(
                $"{DisplayName}'s Archipelago world is not installed.\n\n"
              + "The launcher builds this seed's ROM from a patch that ships "
              + "inside the world, and it is not on this machine yet. Install "
              + $"the {Manifest.ApWorldName} apworld in Archipelago, then press "
              + "Play again.");
        }

        byte[] patched;
        try { patched = LauncherV2.Core.Patching.ApPatch.ApplyRawBsdiff(rom, patch); }
        catch (Exception ex)
        {
            throw new SessionRomRefusedException(
                $"That {DisplayName} ROM could not be patched.\n\n{ex.Message}\n\n"
              + "Check that your copy is one of the dumps this game accepts.");
        }

        string got = Md5Hex(patched);
        if (got != sp.ResultMd5)
        {
            // ⚠ Never hand over a ROM that failed the world's own check. A
            // wrong-but-bootable ROM is the worst outcome there is: the game
            // runs, nothing is ever sent, and the bridge takes the blame.
            throw new SessionRomRefusedException(
                $"The patched {DisplayName} ROM is not what its world expects.\n\n"
              + $"Result {got}, expected {sp.ResultMd5}.\n\n"
              + "That means the ROM you chose is not the dump this patch was "
              + "built against. Choose a different copy in Settings → "
              + $"{DisplayName}.");
        }

        File.WriteAllBytes(outPath, patched);
        SessionRomNote = $"[{DisplayName}] Patched your ROM for this seed and "
                       + "checked it against the world's own hash.";
        return outPath;
    }

    private static string Md5Hex(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)).ToLowerInvariant();

    private string? CopyForSaveIsolation(ApSession session)
    {
        string? seed = GetSeedName?.Invoke();
        if (RomPath is null || !File.Exists(RomPath)
            || string.IsNullOrWhiteSpace(seed)) return null;
        try
        {
            string outPath = Path.Combine(RomLibraryDirectory, "sessions",
                SessionRomName(session.SlotName, seed, Path.GetExtension(RomPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            if (!File.Exists(outPath)) File.Copy(RomPath, outPath);
            return outPath;
        }
        catch { return null; }
    }

    /// Public and static so the rule can be checked without a running game.
    public static string SessionRomName(string slot, string? seed, string ending)
    {
        static string Safe(string s) => string.Concat(
            s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        string tag = string.IsNullOrWhiteSpace(seed) ? "" : "_" + Safe(seed!);
        return Safe(slot) + tag + ending;
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

        // ⚠⚠ FIRST: a world that patches with ONE fixed bsdiff.
        //
        // These games have no per-seed container, so the old path fell
        // straight through to CopyForSaveIsolation and handed the emulator a
        // byte-for-byte copy of the RETAIL ROM. Everything downstream then
        // looked healthy -- the game booted, the client ran, the connector
        // loaded -- and not one check could ever be sent, because the AP hooks
        // the connector reads at 0x80400000 only exist in the patched file.
        // Found 26 Aug 2026 by md5-ing the session ROM London had actually
        // launched: 741a94ee... , the untouched cartridge dump.
        if (Manifest.Rom?.StaticPatch is { } staticPatch)
        {
            string? built = ApplyStaticPatchForSession(staticPatch, session);
            if (built != null) return built;
        }

        string? patch = ResolvePatch(session.SlotName, out string note);

        // The games whose world patches in its own code hand the player a
        // finished ROM, and that is what the store holds for (seed, slot) --
        // there is nothing to apply, the stored file IS this seed's game.
        // Only a file with no patch manifest qualifies: an .ap* container
        // that slipped into the store still goes down the apply path, where
        // the refusal explains itself.
        if (patch is not null && Manifest.PatchModel == "custom"
            && ApPatch.ReadManifest(patch) is null)
        {
            // A zip whose top level is one 16-hex-digit folder is a 3DS
            // layered-FS mod: the SEED lives in that folder, not in the ROM.
            // A Link Between Worlds delivers exactly this -- the player opens
            // the .apalbw with Archipelago's own client, which builds a zip
            // holding 00040000000EC300/.
            string? titleId = ThreeDsModTitleId(patch);
            if (titleId != null)
                return InstallThreeDsModSession(patch, titleId, session);

            SessionRomNote = $"[{DisplayName}] Playing the randomized game file "
                           + $"stored for slot \"{session.SlotName}\".";
            return patch;
        }

        if (patch is null)
        {
            // A world that patches in its own Python never hands the launcher a
            // container to store, so "no patch stored -- no checks will be sent"
            // is the wrong thing to say: the player was told to bring a ROM that
            // is ALREADY patched, and telling them it is unpatched sends them
            // looking for a problem that isn't there.
            if (Manifest.PatchModel != "custom")
            {
                SessionRomNote = note;
                // No patch does not mean no seed. The emulator names its save
                // after the ROM file, so launching the library copy hands
                // every seed one shared save -- the same leak the patched
                // games fixed with SessionRomName. The copy is byte-identical;
                // only its name carries the seed.
                return CopyForSaveIsolation(session);
            }

            // But the launcher must not TAKE THEIR WORD for it either. These
            // games are the one case where nothing downstream can tell a seed
            // ROM from a plain one: there is no container to check, and a RAM
            // map that gates on a save file (rather than an AP signature) will
            // sit there reporting nothing on a vanilla cartridge -- which reads
            // exactly like a broken bridge. So compare against the world's own
            // accepted dump: a byte-identical match is PROOF this is the file
            // the patcher takes as input, not the file it produces.
            if (RomPath != null && Manifest.PatchBaseMd5.Count > 0)
            {
                string actual = ComputeMd5(RomPath);
                if (Manifest.PatchBaseMd5.Contains(actual))
                    throw new SessionRomRefusedException(
                        $"That is the plain {DisplayName} ROM, not this seed's.\n\n"
                      + $"{Path.GetFileName(RomPath)} is byte-for-byte the "
                      + "unmodified game, so nothing in it belongs to seed "
                      + $"\"{GetSeedName?.Invoke() ?? "this"}\" and no check would "
                      + "ever be sent.\n\n"
                      + "This game's world builds its ROM in its own code, which "
                      + "the launcher cannot run. Patch your copy with "
                      + "Archipelago's own client for this seed and slot, then "
                      + $"choose THAT file in Settings → {DisplayName}.");
            }

            SessionRomNote =
                $"[{DisplayName}] This game's world builds its ROM in its own "
              + "code, so the launcher plays the file you chose as-is. Make "
              + "sure it is the ROM Archipelago's own client built for this "
              + "seed and slot -- a plain ROM will send nothing.";
            return null;
        }

        // One output per slot, rebuilt every launch. Patching a 16 MB ROM takes
        // a fraction of a second, and always rebuilding means a re-generated
        // seed can never be played against yesterday's patched copy.
        string ending = ApPatch.ReadManifest(patch)?.ResultFileEnding ?? ".rom";
        string outPath = Path.Combine(RomLibraryDirectory, "sessions",
            SessionRomName(session.SlotName, GetSeedName?.Invoke(), ending));

        try
        {
            // The audited base-ROM hash rides along for legacy delta
            // containers, which name no checksum of their own.
            var result = ApPatch.Apply(patch, RomPath!, outPath,
                Manifest.PatchBaseMd5.Count > 0 ? Manifest.PatchBaseMd5 : null);
            SessionRomNote = $"[{DisplayName}] Patched for slot \"{session.SlotName}\" "
                           + $"({result.Size / (1024 * 1024)} MB, MD5 {result.Md5[..8]}).";
            return outPath;
        }
        catch (NotSupportedException) when (Manifest.PatchModel == "custom")
        {
            // Known at audit time: this world patches in its own code. Say
            // what the player can actually do instead of a bare refusal.
            SessionRomNote =
                $"[{DisplayName}] This game's world builds its ROM in its own "
              + "code, which the launcher cannot run. Patch with Archipelago's "
              + "own client, then choose the patched ROM in Settings -- playing "
              + "continues normally from there.";
            return null;
        }
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

    /// "procedure", "delta" or "custom": the world produces a per-seed file,
    /// so there is something real to ask the player for.
    private bool DeclaresPatchMachinery
        => Manifest.PatchModel is "procedure" or "delta" or "custom";

    public override SeedPatchRequest? GetUnmetSeedPatch(string seed, string slot)
    {
        // A world that carries its own client is never patched by London. The
        // whole chain -- reading memory, applying whatever the game needs,
        // talking to the server -- belongs to that client, and London's part
        // ends at starting the emulator. Asking for a patch here produced a
        // dialog with no right answer: Burnout 3's world contains no patch
        // machinery at all (no APProcedurePatch, no patch suffix, the word
        // "patch" appears nowhere in its source), so the file it demanded
        // could not exist. Now that cancelling stops the join, a question with
        // no answer is not merely noise -- it is an unplayable game.
        // A world that carries its own client still BUILDS a per-seed game
        // when its manifest declares patch machinery -- A Link Between Worlds
        // ships its own client AND an .apalbw. Skipping the question here
        // launched the plain library ROM: the emulator ran, the client
        // connected, and the game was ordinary vanilla. Only a world with no
        // patch machinery at all (Burnout 3: no APProcedurePatch, no patch
        // suffix, the word "patch" nowhere in its source) has no file to ask
        // for, and asking would be a dialog with no right answer.
        if (WorldCarriesOwnClient && !DeclaresPatchMachinery) return null;

        // Asked BEFORE launch, when the seed is known but the plugin has not run
        // yet, so this must not depend on GetSeedName having been called.
        if (SeedPatchStore.For(GameId).Resolve(seed, slot) != null) return null;

        // A world that patches in its own code hands out a finished ROM, not a
        // patch -- so that is what the player is asked for, in those words,
        // with the ROM picker filter rather than *.ap*.
        if (Manifest.PatchModel == "custom")
            return new SeedPatchRequest(DisplayName, seed, slot,
                "Randomized game file or seed archive (*.zip;*.cci;*.3ds;*.nds;"
              + "*.z64;*.sfc;*.gba;*.gbc;*.gb;*.nes;*.iso;*.bin)|*.zip;*.cci;"
              + "*.3ds;*.nds;*.z64;*.sfc;*.gba;*.gbc;*.gb;*.nes;*.iso;*.bin|"
              + "All files (*.*)|*.*",
                WhatToPick: "the randomized file built for this seed "
                          + "(Archipelago's own client builds it from the patch "
                          + "you were given)");

        return new SeedPatchRequest(DisplayName, seed, slot,
            "Archipelago patch (*.ap*)|*.ap*|All files (*.*)|*.*");
    }

    public override string? ImportSeedPatch(string sourcePath, string seed, string slot)
    {
        try
        {
            if (Manifest.PatchModel == "custom")
            {
                // No manifest to read -- the file IS the game. Check what can
                // be checked: it must not be a patch container, and it must
                // not be the vanilla dump the patcher takes as input.
                if (Path.GetExtension(sourcePath)
                        .StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
                    return "That is the patch container, and this game's world "
                         + "only knows how to apply it in its own code. Open "
                         + "the file with Archipelago's own client first -- it "
                         + "builds the finished game file -- then pick THAT here.";

                if (Manifest.PatchBaseMd5.Count > 0
                    && Manifest.PatchBaseMd5.Contains(ComputeMd5(sourcePath)))
                    return $"That is the plain {DisplayName} ROM -- byte-for-byte "
                         + "the unmodified game, so it cannot be this seed's. Pick "
                         + "the file Archipelago's client built for this seed.";

                SeedPatchStore.For(GameId).ImportRaw(sourcePath, seed, slot, ApWorldName);
                return null;
            }

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
    string Description, long Size, IReadOnlyList<string> Md5,
    /// A world that ships ONE fixed bsdiff against the retail dump rather
    /// than building a patch per seed. Its client applies this on the
    /// player's machine; nothing about it needs the world's Python, so
    /// London applies it too and hands the emulator a ROM that actually
    /// carries the AP hooks. Null when the world has no such patch.
    StaticPatchManifest? StaticPatch,
    /// Dumps that are byte-swapped relative to the patch's expected input.
    /// N64 dumps come in both orders and the world swaps before patching;
    /// skip that and bsdiff produces garbage that boots to a black screen.
    IReadOnlyList<string> ByteswapMd5);

/// `entry` is the file inside the apworld; `result_md5` is the world's own
/// answer for what the patched ROM must hash to, so London can prove it
/// produced the same file the world's client would have.
public sealed record StaticPatchManifest(string Entry, string ResultMd5);

/// Who made what. The game and the Archipelago world are other people's work;
/// only the plugin is ours, and the launcher says so on the game's page.
public sealed record CreditsManifest(
    string? GameBy, string? WorldBy, string? WorldUrl,
    IReadOnlyList<string> SetupGuideBy, string? PluginBy,
    /// The people the world names in its own source. Empty when the world
    /// publishes none -- then WorldBy (the licence holder) is all we can say.
    IReadOnlyList<string> WorldAuthors);

public sealed record GameManifest(
    string Id, string DisplayName, string Subtitle, string Platform,
    string ApWorldName, string Description,
    RomSpecManifest? Rom, bool ChecksVerified, string? LuaModule,
    string ClientProtocol,
    // client.kind: "london" (our connector reads memory; Lua map required by
    // the build gate) or "world" (the world ships its own client; London only
    // launches). ClientName is what the world's client is called in the
    // Archipelago Launcher, shown to the player at launch.
    string ClientKind, string? ClientName,
    // The connector the WORLD ships for itself, by filename. A kind=world game
    // on BizHawk has no RAM map of ours, but many of them still need a Lua
    // script loaded in the emulator -- their own client writes it into
    // Archipelago's data/lua/ and expects to find the emulator already running
    // it. Star Fox 64 is the first: without this London started BizHawk with
    // no script at all, so there was nothing for the client to talk to and no
    // check ever moved. Null for worlds that need no script.
    string? WorldConnectorLua,
    // The world's own setting that names the player's ROM, "block.key" in
    // Archipelago's host.yaml. Without it the world's client opens a file
    // dialog on every launch for a ROM London already knows the path to.
    string? WorldRomPathSetting,
    // ⭐ The built-in relay that REPLACES this world's Archipelago client.
    // When it is set London opens the socket the emulator's connector dials
    // and starts no second program at all.
    string? WorldRelay,
    // How this game's world builds its ROM, audited from the world itself:
    // "procedure" (container steps our patcher runs), "delta" (legacy
    // APDeltaPatch -- PatchBaseMd5 holds the world's own base-ROM hash,
    // which the container does not carry), "custom" (the world patches in
    // its own code; the launcher cannot), or "unknown".
    string PatchModel, IReadOnlyList<string> PatchBaseMd5,
    // Who made the game, the world and the guide. Null members are omitted
    // from the credit list rather than guessed at.
    CreditsManifest? Credits)
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
                Str(ro, "description") ?? "your own copy of the game", size, md5,
                StaticPatch(ro), ByteswapMd5(ro));
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
            Str(r, "lua_module"),
            // client.protocol, defaulting to bizhawk. The build tool refuses a
            // manifest whose protocol has no installed bridge, so an unknown
            // value never reaches here -- but the default keeps an older
            // manifest that predates the field loading rather than throwing.
            (r.TryGetProperty("client", out var cl)
                 && cl.ValueKind == JsonValueKind.Object
                 ? Str(cl, "protocol") : null) ?? "bizhawk",
            // kind defaults to "london": every manifest that predates the
            // field is a memory-reading game, and the gate has always
            // guaranteed those carry a Lua map.
            (cl.ValueKind == JsonValueKind.Object ? Str(cl, "kind") : null)
                ?? "london",
            cl.ValueKind == JsonValueKind.Object ? Str(cl, "name") : null,
            // The world's own connector filename, when it ships one.
            cl.ValueKind == JsonValueKind.Object ? Str(cl, "world_lua") : null,
            cl.ValueKind == JsonValueKind.Object ? Str(cl, "rom_path_setting") : null,
            cl.ValueKind == JsonValueKind.Object ? Str(cl, "relay") : null,
            PatchModel(r), PatchMd5(r), Credits(r));

        static CreditsManifest? Credits(JsonElement r)
        {
            if (!r.TryGetProperty("credits", out var c)
                || c.ValueKind != JsonValueKind.Object)
                return null;

            string? WorldName = null, WorldUrl = null;
            var worldAuthors = new List<string>();
            if (c.TryGetProperty("world_by", out var w)
                && w.ValueKind == JsonValueKind.Object)
            {
                WorldName = Str(w, "name");
                WorldUrl = Str(w, "url");
                if (w.TryGetProperty("authors", out var wa)
                    && wa.ValueKind == JsonValueKind.Array)
                    worldAuthors.AddRange(wa.EnumerateArray()
                                            .Where(e => e.ValueKind == JsonValueKind.String)
                                            .Select(e => e.GetString()!));
            }

            var guide = new List<string>();
            if (c.TryGetProperty("setup_guide_by", out var g)
                && g.ValueKind == JsonValueKind.Object
                && g.TryGetProperty("names", out var gn)
                && gn.ValueKind == JsonValueKind.Array)
                guide.AddRange(gn.EnumerateArray()
                                 .Where(e => e.ValueKind == JsonValueKind.String)
                                 .Select(e => e.GetString()!));

            return new CreditsManifest(
                Str(c, "game_by"), WorldName, WorldUrl, guide, Str(c, "plugin_by"),
                worldAuthors);
        }

        static string PatchModel(JsonElement r)
            => r.TryGetProperty("patch", out var p)
               && p.ValueKind == JsonValueKind.Object
               ? Str(p, "model") ?? "unknown" : "unknown";

        static StaticPatchManifest? StaticPatch(JsonElement rom)
        {
            if (!rom.TryGetProperty("static_patch", out var sp)
                || sp.ValueKind != JsonValueKind.Object) return null;
            string? entry = Str(sp, "entry"), md5 = Str(sp, "result_md5");
            if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(md5))
                return null;
            return new StaticPatchManifest(entry!, md5!.ToLowerInvariant());
        }

        static IReadOnlyList<string> ByteswapMd5(JsonElement rom)
            => rom.TryGetProperty("byteswap_md5", out var b)
               && b.ValueKind == JsonValueKind.Array
               ? b.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                  .Select(e => e.GetString()!.ToLowerInvariant()).ToArray()
               : Array.Empty<string>();

        static IReadOnlyList<string> PatchMd5(JsonElement r)
        {
            if (!r.TryGetProperty("patch", out var p)
                || p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("base_md5", out var h)
                || h.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return h.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.ToLowerInvariant())
                    .ToArray();
        }
    }

    static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
}
