using System.IO;
using System.Linq;
using System.Text.Json;

using LauncherV2.Core.Plugins;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Catalog;

// Eet plugin, mange spil. Opfoerslen kommer fra game.json ved siden af
// assemblyen, saa et nyt spil er en datafil - ikke en ny klasse.
//
// ⚠ Manifestet maa GAETTE paa ingenting. Er et felt tomt, siger pluginet det
// til spilleren i stedet for at finde paa en vaerdi. Se catalog/SKEMA.md.
public class GenericEmulatorPlugin : EmulatorPlugin
{
    protected readonly GameManifest Manifest;

    // Manifestet ligger INDE i assemblyen, ikke ved siden af den. pack_plugin.py
    // hvidlister med vilje kun assembly + deps + plugin.json - en loes game.json
    // ville aldrig naa med i pakken, og pluginet ville starte uden at kunne finde
    // sig selv. Som embedded resource kan de to ikke komme fra hinanden.
    public GenericEmulatorPlugin()
    {
        var asm = GetType().Assembly;
        string? name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("game.json",
                                               StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException(
                $"{asm.GetName().Name} has no embedded game.json. The plugin was "
              + "built without its manifest - see catalog/SKEMA.md.");

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
    // LuaModuleName arves: basisklassens default er GameId, som ER Manifest.Id.

    /// ⛔ Bliver foerst true naar RAM-kortet for netop dette spil er maalt i
    /// spillet. Indtil da advarer launcheren ved start, saa ingen sidder en
    /// time i en multiworld uden at der kommer checks.
    public override bool ChecksImplemented => Manifest.ChecksVerified;

    /// Kun spil hvor vi HAR en verificeret hash kan afvise praecist.
    /// Mangler den, faar vi kun stoerrelsen - og saa siger dialogen det.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => Manifest.Rom is { Size: > 0 } r
            ? new[] { new RomIdentity(r.Size, r.Md5, r.Description) }
            : Array.Empty<RomIdentity>();

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;

        string what = Manifest.Rom?.Description ?? $"a {RomSystem} {DisplayName} ROM";
        if (Manifest.Rom?.Md5 is null)
            what += "  —  this edition cannot be checked exactly; "
                  + "make sure yourself that it is the right dump";

        return new RomRequirement(DisplayName, RomSystem, what,
                                  Manifest.Rom?.Md5, WrongVersionPresent: false,
                                  BuildRomFilter());
    }
    // GameBadges arves ogsaa - basisklassens "ROM needed" er praecis rigtig her.
}

public sealed record RomSpecManifest(string Description, long Size, string? Md5);

public sealed record GameManifest(
    string Id, string DisplayName, string Subtitle, string Platform,
    string ApWorldName, string Description,
    RomSpecManifest? Rom, bool ChecksVerified)
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
            string? md5 = ro.TryGetProperty("md5", out var m)
                       && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
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
                && c.ValueKind == JsonValueKind.True);
    }

    static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
}
