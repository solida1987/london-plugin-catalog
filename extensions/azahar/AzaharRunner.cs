using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Azahar;

// The 3DS, through Azahar.
//
// London reads no memory here. The 3DS worlds ship their own Archipelago
// client, and that client speaks Azahar's own scripting protocol -- a UDP
// request/response on 127.0.0.1:45987 carrying read and write packets. So this
// extension does what SohRunner and DaxanaduRunner do: find the emulator, set
// the one thing that has to be set, start it, and get out of the way.
//
// ⚠⚠ THE ONE THING THAT HAS TO BE SET. Azahar declares enable_rpc_server among
// its [Debugging] keys and starts the scripting server only when it is true.
// Left alone, the emulator runs perfectly and answers nothing on 45987 -- the
// world's client sits there timing out, which reads as a broken bridge rather
// than an unticked box. This is exactly how snes9x behaved on 19 Aug 2026:
// right ROM, real session, not one check. So the config is written before the
// process starts, and for the same reason as there -- the emulator rewrites
// this file itself on exit and would undo an edit made while it runs.
//
// ⛔ WE SHIP NOTHING. Not Azahar (GPL-2.0 and free, but the player's to fetch
// or to accept an offer for), not 3DS system files, and above all not a game.
public sealed class AzaharRunner : IEmulatorBridge
{
    public const string Folder = "Azahar";

    public string   Protocol    => "azahar";
    public string   DisplayName => "Azahar (3DS)";
    public string[] Systems     => new[] { "3DS", "N3DS" };
    public string   HomepageUrl => "https://github.com/azahar-emu/azahar/releases";

    /// True: this extension promises a launch with the scripting server on,
    /// and both halves are implemented. It promises no transport, because the
    /// world's own client is the transport.
    public bool IsReady => true;

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(
            Folder, "Azahar", HomepageUrl, "azahar.exe",
            new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the Azahar team (a fork of Citra)",
                Licence:      "GPL-2.0",
                LicenceUrl:   "https://github.com/azahar-emu/azahar/blob/master/license.txt",
                DownloadPage: HomepageUrl,
                Owner:        "azahar-emu",
                Repo:         "azahar",
                // Their Windows builds come in three toolchains; msvc is the
                // conventional one. The version sits in the middle of the name,
                // so the pattern has to wildcard it.
                AssetPattern: "azahar-windows-msvc-*.zip")),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is null)
            return "Azahar is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that "
                 + "azahar.exe sits directly in that folder, or accept the "
                 + "offer to fetch it. It is free and GPL-2.0: "
                 + $"{HomepageUrl}\n\n"
                 + "Azahar also needs the 3DS system files that only your own "
                 + "console can produce. Those are yours to supply; we never "
                 + "distribute them.";
        return null;
    }

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        string dir = Path.GetDirectoryName(exe)!;
        EnableRpcServer(dir);
        ConfigureSessionStorage(dir, context.RomPath);

        // The ROM goes on the command line; Azahar loads it straight away.
        return new LaunchPlan(exe, $"\"{context.RomPath}\"", dir);
    }

    /// Where Azahar keeps qt-config.ini.
    ///
    /// A `user` folder beside the executable makes it portable, which is what
    /// an install under Emulators\ should be: the settings belong to this
    /// launcher's copy, not to whatever the player already runs elsewhere.
    /// Creating the folder is what turns portable mode on, so this both finds
    /// the file and decides where it lives.
    private static string ConfigPath(string emulatorDir)
        => Path.Combine(emulatorDir, "user", "config", "qt-config.ini");

    /// Switch Azahar's scripting server on in its own config file.
    ///
    /// Best effort throughout: an unwritable config is not a reason to refuse
    /// to launch. The world client's own connect attempt is what actually
    /// decides whether the session syncs, and its timeout message is clearer
    /// than anything we could throw from here.
    internal static void EnableRpcServer(string emulatorDir)
    {
        try
        {
            string conf = ConfigPath(emulatorDir);
            Directory.CreateDirectory(Path.GetDirectoryName(conf)!);

            if (!File.Exists(conf))
            {
                // First run: Azahar has not written its config yet. Seeding
                // just this section is enough -- it fills in every other
                // default itself and keeps what it finds here.
                File.WriteAllText(conf,
                    "[Debugging]"              + Environment.NewLine +
                    "enable_rpc_server=true"   + Environment.NewLine +
                    "enable_rpc_server\\default=false" + Environment.NewLine);
                return;
            }

            var lines = File.ReadAllLines(conf).ToList();
            int section = lines.FindIndex(l =>
                l.Trim().Equals("[Debugging]", StringComparison.OrdinalIgnoreCase));

            if (section < 0)
            {
                lines.Add("[Debugging]");
                lines.Add("enable_rpc_server=true");
                File.WriteAllLines(conf, lines);
                return;
            }

            // Only inside [Debugging]: the same key name must not be hunted
            // for across the whole file, where another section could own it.
            int end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith("["));
            if (end < 0) end = lines.Count;

            int key = -1;
            for (int i = section + 1; i < end; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("enable_rpc_server=", StringComparison.OrdinalIgnoreCase))
                { key = i; break; }
            }

            if (key >= 0) lines[key] = "enable_rpc_server=true";
            else          lines.Insert(section + 1, "enable_rpc_server=true");

            File.WriteAllLines(conf, lines);
        }
        catch
        {
            // Unwritable config -- the launch still goes ahead.
        }
    }

    /// Point Azahar's SD card at a folder named after the ROM file.
    ///
    /// 3DS saves live on the emulated SD card, keyed by title id -- NOT by ROM
    /// file name -- so two seeds of the same game share one save unless the
    /// card itself moves. The session ROM's name carries the slot and seed, so
    /// a card per ROM stem is a card per seed. Measured contract, from the
    /// config Azahar itself wrote on 25 Aug 2026: section [Data%20Storage],
    /// keys use_custom_storage and sdmc_directory, forward slashes accepted.
    /// The NAND (system files) stays shared on purpose.
    internal static void ConfigureSessionStorage(string emulatorDir, string romPath)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(romPath);
            if (stem.Length == 0) return;
            string card = Path.Combine(emulatorDir, "user", "sdmc_sessions", stem)
                              .Replace('\\', '/') + "/";
            Directory.CreateDirectory(card);

            string conf = Path.Combine(emulatorDir, "user", "config", "qt-config.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(conf)!);

            var lines = File.Exists(conf)
                ? File.ReadAllLines(conf).ToList()
                : new List<string>();

            SetKey(lines, "Data%20Storage", "use_custom_storage", "true");
            SetKey(lines, "Data%20Storage", "sdmc_directory", card);
            File.WriteAllLines(conf, lines);
        }
        catch
        {
            // Unwritable config -- the launch still goes ahead; the cost is a
            // shared card, not a refused game.
        }
    }

    /// Set one key inside one section, creating the section at the end when it
    /// is missing. Only that section is touched: the same key name under
    /// another section could belong to someone else.
    private static void SetKey(List<string> lines, string section, string key,
                               string value)
    {
        int start = lines.FindIndex(l =>
            l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
            return;
        }
        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
        if (end < 0) end = lines.Count;
        for (int i = start + 1; i < end; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + "=",
                    StringComparison.OrdinalIgnoreCase))
            { lines[i] = key + "=" + value; return; }
        }
        lines.Insert(start + 1, key + "=" + value);
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "3DS worlds talk to Archipelago through their own client, which "
            + "speaks Azahar's scripting protocol directly; London does not "
            + "read this emulator's memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "3DS worlds talk to Archipelago through their own client, which "
            + "speaks Azahar's scripting protocol directly; London does not "
            + "write this emulator's memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
