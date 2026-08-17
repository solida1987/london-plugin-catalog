using System.IO;

using LauncherV2.Core.Plugins;

namespace LauncherV2.Tools.InstallPlugin;

/// InstallPlugin <launcher folder> <package.londonplugin> [more packages...]
///
/// Does exactly what the launcher's "Add plugin…" button does -- unpack into
/// GamePlugins/ and record the approval in Data/plugins.json -- but from the
/// command line, against a launcher folder that is not this program's own.
///
/// This exists so a test install can be prepared without a human clicking
/// through the consent dialog for every rebuild. It is a DEVELOPMENT tool: the
/// dialog is the point for a real player, because it is where they see what the
/// plugin declares before it ever runs. Nothing here is shipped.
///
/// Every step calls the launcher's own code -- Inspect, Install, HashDirectory,
/// Approve. Re-implementing the directory hash here would be the obvious way to
/// end up with an approval the launcher then rejects as tampered.
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: InstallPlugin <launcher folder> <package> [package...]");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        if (!File.Exists(Path.Combine(root, "Multiworld Launcher.exe")))
        {
            Console.WriteLine($"FAIL  {root} does not look like a launcher install "
                            + "(no Multiworld Launcher.exe).");
            return 1;
        }

        // PluginPackage.RootDirectory and PluginTrustStore.FilePath both hang off
        // AppContext.BaseDirectory, which is normally where THIS exe sits. Point
        // it at the target install so the launcher's own code writes to the right
        // GamePlugins/ and Data/ without any path being duplicated here.
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY",
                                        root + Path.DirectorySeparatorChar);

        if (Path.GetFullPath(PluginPackage.RootDirectory)
            != Path.GetFullPath(Path.Combine(root, "GamePlugins")))
        {
            Console.WriteLine("FAIL  could not redirect the base directory — "
                            + $"the launcher code still resolves to {PluginPackage.RootDirectory}");
            return 1;
        }

        int failed = 0;
        foreach (string package in args.Skip(1))
        {
            Console.WriteLine();
            Console.WriteLine(Path.GetFileName(package));

            var candidate = PluginPackage.Inspect(package);
            if (!candidate.IsUsable)
            {
                Console.WriteLine($"  FAIL  {candidate.Error}");
                failed++;
                continue;
            }

            var m = candidate.Manifest!;
            Console.WriteLine($"  game       {m.DisplayName}  ({m.GameId} {m.Version})");
            Console.WriteLine($"  author     {m.Author}");
            Console.WriteLine($"  package    {candidate.ShortHash}");

            string? error = PluginPackage.Install(candidate);
            if (error != null)
            {
                Console.WriteLine($"  FAIL  {error}");
                failed++;
                continue;
            }

            // The trust record hashes the INSTALLED FOLDER, not the package: it
            // is what the launcher re-hashes at every start to notice an edit.
            string dir = PluginPackage.DirectoryFor(m.GameId);
            string dirHash = PluginPackage.HashDirectory(dir);
            PluginTrustStore.Approve(m.GameId, dirHash, m.Version, m.Author);

            var verdict = PluginTrustStore.Check(m.GameId, dir);
            Console.WriteLine(verdict == PluginTrustStore.Verdict.Trusted
                ? $"  OK    installed and approved — {verdict}"
                : $"  FAIL  approved but the launcher would still refuse it: {verdict}");
            if (verdict != PluginTrustStore.Verdict.Trusted) failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"GamePlugins: {PluginPackage.RootDirectory}");
        Console.WriteLine($"approvals:   {PluginTrustStore.FilePath}");
        return failed == 0 ? 0 : 1;
    }
}
