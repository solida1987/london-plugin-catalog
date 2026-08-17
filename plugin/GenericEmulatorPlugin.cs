using System.IO;
using System.Linq;
using System.Text.Json;

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
    // LuaModuleName is inherited: the base default is GameId, which IS Manifest.Id.

    /// Only becomes true once the RAM map for THIS game has been measured
    /// in-game. Until then the launcher warns at launch, so nobody sits in a
    /// multiworld for an hour with no checks arriving.
    public override bool ChecksImplemented => Manifest.ChecksVerified;

    /// Only a game we HAVE a verified hash for can reject precisely. Without
    /// one we have size alone -- and then the dialog says so.
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
    // GameBadges is inherited too -- the base "ROM needed" is exactly right here.
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
