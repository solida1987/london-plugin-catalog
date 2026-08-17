using System;
using System.IO;
using System.Linq;
using System.Reflection;

using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Tools.RomCheck;

/// Ask a built plugin the question a player's ROM actually gets asked.
///
///     RomCheck &lt;plugin.dll&gt; &lt;file-to-offer&gt; [--expect accept|reject]
///
/// Exit 0 when the answer matches --expect (or when none was given). This is
/// the only place the "size unknown, MD5 known" path is exercised, and that
/// path decides whether a player can start the game at all.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("RomCheck <plugin.dll> <file> [--expect accept|reject]");
            return 2;
        }

        string dll = Path.GetFullPath(args[0]);
        string file = Path.GetFullPath(args[1]);
        string? expect = null;
        int i = Array.IndexOf(args, "--expect");
        if (i >= 0 && i + 1 < args.Length) expect = args[i + 1];

        // Load beside the launcher that is already in this process, so the
        // plugin's EmulatorPlugin is the SAME type as ours -- the cast below
        // is exactly the one the launcher makes.
        var asm = Assembly.LoadFrom(dll);
        var type = asm.GetTypes().FirstOrDefault(t =>
            !t.IsAbstract && typeof(EmulatorPlugin).IsAssignableFrom(t));
        if (type is null)
        {
            Console.WriteLine($"no EmulatorPlugin in {Path.GetFileName(dll)}");
            return 2;
        }

        var plugin = (EmulatorPlugin)Activator.CreateInstance(type)!;
        string? reason = plugin.ValidateBaseRom(file);
        bool accepted = reason is null;

        Console.WriteLine($"  plugin : {plugin.GameId}");
        Console.WriteLine($"  file   : {Path.GetFileName(file)} " +
                          $"({new FileInfo(file).Length} bytes)");
        Console.WriteLine($"  verdict: {(accepted ? "ACCEPTED" : "REJECTED")}");
        if (reason is not null)
            Console.WriteLine("  reason : " + reason.Replace("\n", "\n           "));

        if (expect is null) return 0;
        bool ok = (expect == "accept") == accepted;
        Console.WriteLine($"  expected {expect} -> {(ok ? "OK" : "MISMATCH")}");
        return ok ? 0 : 1;
    }
}
