using System;
using System.IO;
using System.Linq;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Tools.ExtensionCheck;

/// Gate: an extension must load, register, and answer honestly.
///
///     ExtensionCheck &lt;extensions-dir&gt; &lt;protocol&gt;
///
/// The interesting answer is the NEGATIVE one. A game whose protocol has no
/// working bridge must be REFUSED with a reason a player can act on -- the
/// failure this whole design exists to prevent is a game that starts, runs, and
/// never sends a check.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("ExtensionCheck <extensions-dir> <protocol>");
            return 2;
        }

        // BridgeRegistry reads from AppContext.BaseDirectory/Extensions; point
        // it at the folder under test by loading from there directly.
        string dir = Path.GetFullPath(args[0]);
        string protocol = args[1];

        // Given a .londonextension instead of a folder, take the package the
        // whole way a player would: look inside, install, then see whether the
        // registry actually picks it up. Inspect+Install are otherwise only
        // reachable through the UI, which is exactly where a gate cannot go.
        if (File.Exists(dir) && dir.EndsWith(ExtensionPackage.Extension,
                                             StringComparison.OrdinalIgnoreCase))
        {
            var cand = ExtensionPackage.Inspect(dir);
            Console.WriteLine($"  package  : {Path.GetFileName(dir)}");
            Console.WriteLine($"  sha256   : {cand.ShortHash}");
            if (!cand.IsUsable) { Console.WriteLine("  FAIL: " + cand.Error); return 1; }
            Console.WriteLine($"  manifest : {cand.Manifest!.ExtensionId} "
                            + $"protocol={cand.Manifest.Protocol}");

            string? err = ExtensionPackage.Install(cand);
            if (err != null) { Console.WriteLine("  FAIL install: " + err); return 1; }
            Console.WriteLine("  installed into " + ExtensionPackage.DirectoryFor(
                cand.Manifest.ExtensionId));
            dir = BridgeRegistry.Directory;
        }

        Console.WriteLine($"  extensions dir : {dir}");
        Console.WriteLine($"  exists         : {Directory.Exists(dir)}");

        var problems = LoadFrom(dir);
        foreach (string p in problems)
            Console.WriteLine($"  [problem] {p}");

        Console.WriteLine($"  installed      : {BridgeRegistry.Installed.Count}");
        foreach (var e in BridgeRegistry.Installed)
            Console.WriteLine($"    - {e.Manifest.ExtensionId}  protocol="
                            + $"{e.Bridge.Protocol}  ready={e.Bridge.IsReady}");

        bool served = BridgeRegistry.CanServe(protocol);
        Console.WriteLine($"  CanServe({protocol}) : {served}");

        string? why = BridgeRegistry.ExplainMissing(protocol, "a test game");
        Console.WriteLine("  explanation    : "
            + (why is null ? "(none - it can run)"
                           : "\n      " + why.Replace("\n", "\n      ")));

        // Installing a bridge must make its emulator folders and notes appear,
        // so the player is never left guessing where a newly supported emulator
        // goes. This is the mechanism, exercised rather than assumed.
        LauncherV2.Plugins.Emulated.EmulatorPlugin.EnsureEmulatorFolders();
        string emus = Path.Combine(AppContext.BaseDirectory, "Emulators");
        Console.WriteLine($"  emulator folders in {emus}:");
        if (Directory.Exists(emus))
            foreach (string d in Directory.GetDirectories(emus).OrderBy(x => x))
            {
                string[] notes = Directory.GetFiles(d, "PUT * HERE.txt");
                Console.WriteLine($"    - {Path.GetFileName(d),-12} "
                    + (notes.Length > 0
                        ? "note: " + Path.GetFileName(notes[0])
                        : "NO NOTE"));
            }

        foreach (var e in BridgeRegistry.Installed)
            foreach (var need in e.Bridge.Emulators)
            {
                string want = Path.Combine(emus, need.FolderName);
                if (!need.IsSafeFolderName)
                {
                    Console.WriteLine($"  OK: refused unsafe folder name "
                                    + $"\"{need.FolderName}\"");
                    continue;
                }
                if (!Directory.Exists(want))
                {
                    Console.WriteLine($"  FAIL: {need.DisplayName} asked for "
                                    + $"{need.FolderName}\\ and it was not created");
                    return 1;
                }
            }

        // Reading where the program lives and running it FROM THERE is the
        // whole point of the folder-and-note mechanism, so it gets exercised:
        // absent -> no plan; present -> a plan pointing inside that folder.
        foreach (var e in BridgeRegistry.Installed)
        {
            var ctx = new BridgeContext("probe", "N64", "", emus);
            var before = e.Bridge.GetLaunchPlan(ctx, emus);
            if (e.Bridge.Emulators.Count == 0) continue;

            var need = e.Bridge.Emulators[0];
            string exe = Path.Combine(emus, need.FolderName, need.ExeName);
            Console.WriteLine($"  launch plan for {e.Manifest.ExtensionId}:");
            Console.WriteLine($"    without the file : "
                            + (before is null ? "none (correct)" : "A PLAN?!"));

            File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });   // stand-in
            var after = e.Bridge.GetLaunchPlan(ctx, emus);
            File.Delete(exe);

            if (after is null)
            {
                Console.WriteLine("    with the file    : still none");
                if (e.Bridge.Protocol.StartsWith("native"))
                {
                    Console.WriteLine("  FAIL: a native runner must resolve its own program");
                    return 1;
                }
                Console.WriteLine("    (bridge attaches to a program the player starts - correct)");
            }
            else
            {
                bool inside = after.ExePath.StartsWith(
                    Path.Combine(emus, need.FolderName), StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"    with the file    : {after.ExePath}");
                Console.WriteLine($"    working dir      : {after.WorkingDirectory}");
                if (!inside)
                {
                    Console.WriteLine("  FAIL: launch plan points outside the player's folder");
                    return 1;
                }
                Console.WriteLine("  OK: resolved inside the folder the player filled");
            }
        }

        // A bridge that is installed but not ready MUST NOT be servable, and
        // MUST produce an explanation. Silence here would be the bug.
        var found = BridgeRegistry.Find(protocol);
        if (found is not null && !found.IsReady)
        {
            if (served) { Console.WriteLine("  FAIL: not ready but reported servable"); return 1; }
            if (why is null) { Console.WriteLine("  FAIL: not ready but no explanation"); return 1; }
            Console.WriteLine("  OK: installed, not ready, refused with a reason");
        }
        else if (found is null && !BridgeRegistry.BuiltIn.Contains(protocol))
        {
            if (why is null) { Console.WriteLine("  FAIL: absent but no explanation"); return 1; }
            Console.WriteLine("  OK: absent, refused with a reason");
        }

        return 0;
    }

    /// BridgeRegistry.LoadInstalled() reads its own folder; for a gate we want
    /// to point at a scratch directory instead, so the real one is temporarily
    /// swapped by running the check with that folder as the base directory.
    private static System.Collections.Generic.IReadOnlyList<string> LoadFrom(string dir)
    {
        string want = BridgeRegistry.Directory;
        if (!string.Equals(Path.GetFullPath(want), dir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(want);
            foreach (string src in Directory.GetDirectories(dir))
            {
                string dst = Path.Combine(want, Path.GetFileName(src));
                Directory.CreateDirectory(dst);
                foreach (string f in Directory.GetFiles(src))
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
        }
        return BridgeRegistry.LoadInstalled();
    }
}
