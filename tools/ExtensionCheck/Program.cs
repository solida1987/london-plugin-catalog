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
