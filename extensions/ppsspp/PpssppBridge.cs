using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core.Extensions;

namespace LauncherV2.Extensions.Ppsspp;

// PPSSPP, shipped as an installable extension like every other bridge.
//
// WHY THIS IS A LAUNCH BRIDGE AND NOT A MEMORY ONE
// ────────────────────────────────────────────────
// Monster Hunter Freedom Unite and K-On! After School Live!! both run here, and both worlds talk to PPSSPP's debugger WebSocket themselves (ws://<host>:<port>/debugger, discovered through report.ppsspp.org/match/list). So this bridge only has to start the emulator with the right disc. Both setup guides ask the player to open the ISO by hand; London passes it on the command line instead.
//
// So ReadAsync/WriteAsync stay unsupported on purpose. A bridge that pretended
// to offer them would be advertising a transport nothing here can speak, and
// the first game to ask would fail somewhere far away from the cause.
public sealed class PpssppBridge : IEmulatorBridge
{
    public const string Folder = "PPSSPP";
    private const string Exe = "PPSSPPWindows64.exe";

    public string   Protocol    => "ppsspp";
    public string   DisplayName => "PPSSPP";
    public string   HomepageUrl => "https://www.ppsspp.org";
    public string[] Systems     => new[] { "PSP" };

    /// ⚠ FALSE until a game has been played through it.
    ///
    /// The command line below is read out of the world's own client and its
    /// setup guide, but nobody has started PPSSPP from London yet. Claiming
    /// otherwise would put the game in the shop as working on the strength of
    /// code review alone.
    public bool IsReady => false;

    private static readonly LauncherV2.Core.Emulators.EmulatorSource Source =
        new(Author:       "Henrik Rydgard and the PPSSPP contributors",
            Licence:      "GPL-2.0-or-later for PPSSPP itself; parts of the bundled PSPSDK are BSD-licensed, and its LICENSE.TXT opens with that notice",
            LicenceUrl:   "https://github.com/hrydgard/ppsspp/blob/master/LICENSE.TXT",
            DownloadPage: "https://www.ppsspp.org/download/",
            Owner:        "hrydgard",
            Repo:         "ppsspp",
            AssetPattern: "Windows-x64.zip");

    public IReadOnlyList<EmulatorRequirement> Emulators => new[]
    {
        new EmulatorRequirement(Folder, "PPSSPP", HomepageUrl, Exe, Source),
    };

    public string? GetUnmetRequirement()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Emulators");
        if (Emulators[0].Resolve(root) is null)
            return "PPSSPP is not in place yet.\n\n"
                 + $"Put your own copy in Emulators\\{Folder}\\ so that {Exe} sits "
                 + $"directly in that folder. You can get it from {HomepageUrl}, or let "
                 + "the launcher fetch that same file from the project's own release once "
                 + "you have seen who wrote it and under what licence.";

        return null;
    }

    /// What the player still has to do in PPSSPP's own settings.
    ///
    /// ⚠ Said, not silently written. We do not know this emulator's config
    /// format well enough to edit it without risking the player's own
    /// settings, and a clear instruction beats a half-correct edit.
    public string ManualSetupNote => "";   // London turns the debugger on itself, in ppsspp.ini

    public LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot)
    {
        string? exe = Emulators[0].Resolve(emulatorsRoot);
        if (exe is null) return null;

        // ⚠⚠ THE REMOTE DEBUGGER IS OFF BY DEFAULT, and both worlds need it.
        //
        // Their setup guides ask the player to do it by hand ("Tools ->
        // Developer tools -> Allow remote debugger"). Skip it and everything
        // looks healthy -- PPSSPP starts, the game runs -- while the client
        // never finds the emulator at all. That is the snes9x NWA trap again.
        //
        // One ini key closes the whole loop, traced through PPSSPP's own
        // source 27 Aug 2026:
        //   RemoteDebuggerOnStartup=True   ([General], ppsspp.ini)
        //     -> UI/NativeApp.cpp:832  flags |= WebServerFlags::DEBUGGER
        //     -> Core/WebServer.cpp    http->Listen(iRemoteISOPort)   (0 = any free port)
        //     -> Core/WebServer.cpp:819 RegisterServer(http->Port())
        //          announces to report.ppsspp.org/match/update
        //     -> which is exactly the list both worlds read to find PPSSPP.
        EnableRemoteDebugger(Path.GetDirectoryName(exe)!);

        string args = $"\"{context.RomPath}\"";
        if (context.Fullscreen) args += " --fullscreen";

        return new LaunchPlan(exe, args, Path.GetDirectoryName(exe)!,
                              new Dictionary<string, string>());
    }


    /// Turn on PPSSPP's debugger web server, keeping every other line the
    /// player has set.
    ///
    /// ⚠ The ini is NOT beside the exe. PPSSPP is portable unless an
    /// installed.txt sits next to it (Windows/main.cpp): portable puts the
    /// memory stick in <exe>/memstick, an installed copy puts it under
    /// Documents/PPSSPP. Either way the config lands in PSP/SYSTEM/ppsspp.ini.
    /// Writing to the wrong one is silent -- the file appears, PPSSPP never
    /// reads it.
    private static void EnableRemoteDebugger(string exeDir)
    {
        try
        {
            string memstick = Path.Combine(exeDir, "memstick");
            if (File.Exists(Path.Combine(exeDir, "installed.txt")))
            {
                // installed.txt may name the memory stick itself; an empty
                // file means "use Documents".
                string named = File.ReadAllText(Path.Combine(exeDir, "installed.txt"))
                                   .Trim().TrimStart('\uFEFF');
                memstick = named.Length > 0
                    ? named
                    : Path.Combine(Environment.GetFolderPath(
                          Environment.SpecialFolder.MyDocuments), "PPSSPP");
            }

            string dir = Path.Combine(memstick, "PSP", "SYSTEM");
            Directory.CreateDirectory(dir);
            string ini = Path.Combine(dir, "ppsspp.ini");

            var lines = File.Exists(ini)
                ? new List<string>(File.ReadAllLines(ini))
                : new List<string>();

            // PPSSPP writes booleans as "True"/"False" with a capital letter
            // (IniFile.h: Set(key, bool) -> newValue ? "True" : "False"), so
            // that is what we write too.
            SetInSection(lines, "General", "RemoteDebuggerOnStartup", "True");
            File.WriteAllLines(ini, lines);
        }
        catch { /* best effort: the player can still flip it in Developer tools */ }
    }

    /// Set one key inside one [Section], adding either if missing.
    private static void SetInSection(List<string> lines, string section,
                                     string key, string value)
    {
        string header = "[" + section + "]";
        int at = lines.FindIndex(l => l.Trim() == header);
        if (at < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add(header);
            lines.Add($"{key} = {value}");
            return;
        }

        // The section ends at the next [Header] -- never write past it.
        int end = lines.Count;
        for (int i = at + 1; i < lines.Count; i++)
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal)) { end = i; break; }

        for (int i = at + 1; i < end; i++)
            if (lines[i].TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && lines[i].Contains('='))
            { lines[i] = $"{key} = {value}"; return; }

        lines.Insert(end, $"{key} = {value}");
    }

    public Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct)
        => Task.FromResult(true);

    public Task<byte[]> ReadAsync(long address, int length, CancellationToken ct)
        => throw new NotSupportedException(
            "PPSSPP is started by London, but the game's own Archipelago world "
            + "carries the code that reads its memory.");

    public Task WriteAsync(long address, byte[] data, CancellationToken ct)
        => throw new NotSupportedException(
            "PPSSPP is started by London, but the game's own Archipelago world "
            + "carries the code that writes its memory.");

    public Task DisconnectAsync() => Task.CompletedTask;
}
