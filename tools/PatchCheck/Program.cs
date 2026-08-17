using System.IO;
using System.Security.Cryptography;

using LauncherV2.Core;
using LauncherV2.Core.Patching;

namespace LauncherV2.Tools.PatchCheck;

/// PatchCheck <patch> <base rom> <out rom> [expected md5]
///
/// Exit 0 when the patch applied and (when given) the result matched the
/// expected MD5. Anything else is exit 1 with the reason on stdout.
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--store")
            return StoreCheck(args.Skip(1).ToArray());

        if (args.Length < 3)
        {
            Console.WriteLine("usage: PatchCheck <patch> <base rom> <out rom> [expected md5]");
            Console.WriteLine("       PatchCheck --store <launcher folder> <gameId> <apworld> "
                            + "<seed> <right patch> <wrong-slot patch>");
            return 1;
        }

        string patch = args[0], baseRom = args[1], outRom = args[2];
        string? expected = args.Length > 3 ? args[3].ToLowerInvariant() : null;

        if (!File.Exists(patch))   { Console.WriteLine($"FAIL  no patch: {patch}"); return 1; }
        if (!File.Exists(baseRom)) { Console.WriteLine($"FAIL  no base ROM: {baseRom}"); return 1; }

        var manifest = ApPatch.ReadManifest(patch);
        if (manifest is null)
        {
            Console.WriteLine("FAIL  not an Archipelago patch (no archipelago.json)");
            return 1;
        }

        Console.WriteLine($"  game        {manifest.Game}");
        Console.WriteLine($"  slot        {manifest.PlayerName ?? "(none)"}");
        Console.WriteLine($"  base md5    {(manifest.BaseChecksums.Count == 0 ? "(not stated)" : string.Join(" or ", manifest.BaseChecksums))}");
        Console.WriteLine($"  steps       {string.Join(" -> ", manifest.Procedure.Select(s => s.Name))}");

        string yours = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(baseRom)))
                              .ToLowerInvariant();
        Console.WriteLine($"  your rom    {yours}");

        ApPatch.Result result;
        var started = DateTime.UtcNow;
        try
        {
            result = ApPatch.Apply(patch, baseRom, outRom);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        var took = DateTime.UtcNow - started;

        Console.WriteLine($"  patched     {result.Md5}  ({result.Size:N0} bytes, "
                        + $"{took.TotalSeconds:F1}s)");

        if (expected is null)
        {
            Console.WriteLine("OK    patch applied (no expected MD5 given, so nothing was compared)");
            return 0;
        }

        if (result.Md5 != expected)
        {
            Console.WriteLine($"FAIL  expected {expected}");
            Console.WriteLine("      the C# patcher and Archipelago's own patcher disagree");
            return 1;
        }

        Console.WriteLine("OK    byte-for-byte identical to Archipelago's own patcher");
        return 0;
    }

    /// Exercises SeedPatchStore end to end, including the refusals.
    ///
    /// The store is the thing standing between "one question per seed" and
    /// "silently played someone else's patch", and its guards only ever fire on
    /// a mistake — so they have to be provoked deliberately or they are never
    /// executed at all before a player hits them.
    static int StoreCheck(string[] a)
    {
        if (a.Length < 6)
        {
            Console.WriteLine("usage: PatchCheck --store <launcher folder> <gameId> "
                            + "<apworld> <seed> <right patch> <wrong-slot patch>");
            return 1;
        }
        string root = Path.GetFullPath(a[0]);
        string gameId = a[1], apworld = a[2], seed = a[3];
        string rightPatch = a[4], wrongPatch = a[5];

        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY",
                                        root + Path.DirectorySeparatorChar);

        var right = ApPatch.ReadManifest(rightPatch);
        var wrong = ApPatch.ReadManifest(wrongPatch);
        if (right?.PlayerName is null || wrong?.PlayerName is null)
        {
            Console.WriteLine("FAIL  both patches must carry a player_name");
            return 1;
        }
        string slot = right.PlayerName!;
        Console.WriteLine($"  seed {seed}, slot \"{slot}\" (wrong patch is \"{wrong.PlayerName}\")");

        var store = SeedPatchStore.For(gameId);
        int bad = 0;

        // 1. Unknown before anything is imported.
        if (store.Resolve(seed, slot) != null)
        {
            Console.WriteLine("  note  a patch was already stored for this seed/slot; "
                            + "the 'asks once' step cannot be observed on this run");
        }

        // 2. Import the right one, and find it again.
        try
        {
            string stored = store.Import(rightPatch, seed, slot, apworld);
            Console.WriteLine($"  ok    imported -> {Path.GetFileName(stored)}");
        }
        catch (Exception ex) { Console.WriteLine($"  FAIL  import refused: {ex.Message}"); bad++; }

        string? found = store.Resolve(seed, slot);
        Console.WriteLine(found != null
            ? "  ok    resolved again — the player is not asked twice"
            : "  FAIL  stored but not resolvable");
        if (found == null) bad++;

        // 3. A patch for ANOTHER slot must be refused.
        try
        {
            store.Import(wrongPatch, seed, slot, apworld);
            Console.WriteLine("  FAIL  accepted a patch belonging to another slot");
            bad++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ok    refused another slot's patch: "
                            + ex.Message.Split('\n')[0]);
        }

        // 4. A patch for another GAME must be refused.
        try
        {
            store.Import(rightPatch, seed, slot, "Some Other Game");
            Console.WriteLine("  FAIL  accepted a patch for a different game");
            bad++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ok    refused a different game's patch: "
                            + ex.Message.Split('\n')[0]);
        }

        // 5. A DIFFERENT seed must still be unknown — the whole point of keying
        //    on the seed rather than on the slot name alone.
        Console.WriteLine(store.Resolve(seed + "_other", slot) == null
            ? "  ok    a different seed is still unknown"
            : "  FAIL  a different seed resolved to this patch");
        if (store.Resolve(seed + "_other", slot) != null) bad++;

        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "OK    store accepts what it should and refuses what it should"
                                   : $"FAIL  {bad} problem(s)");
        return bad == 0 ? 0 : 1;
    }
}
