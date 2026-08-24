using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Plugins.Catalog;

/// Did the game close?
///
/// The emulator plugins own the process they started, so Process.Exited tells
/// them. A PC game started through `steam://rungameid/…` gives us nothing to
/// hold: Steam's protocol handler returns immediately and the game is its
/// child, not ours. London used to set IsRunning = true at launch and never
/// clear it, so the session sat at "Playing" long after the player had quit —
/// with the AP slot still connected and the relay still holding its port.
///
/// So watch for the game's own process instead, found by looking at what
/// executables live in the game's folder. Two rules keep this honest:
///
///   • Only a process that we SAW alive and then saw gone counts as an exit.
///     Never seeing it means we do not know, and "I do not know" must not be
///     reported as "the player quit" — that would tear down a live session.
///   • A process only counts when its image really sits inside the game's
///     folder. Matching on name alone would let another copy of a
///     similarly-named program stand in for the game.
public sealed class GameWatcher : IDisposable
{
    /// What the watcher believes, and how it is allowed to change.
    public enum State
    {
        /// Launched; the game has not shown up yet. Steam can take a while.
        WaitingForGame,
        /// Seen alive. From here, disappearing means the player quit.
        Running,
        /// Was running, now gone. Terminal, and the only state that ends a session.
        Exited,
        /// Never showed up within the grace period. Terminal, and it means
        /// London cannot track this game — not that the game closed.
        NeverAppeared,
    }

    /// The whole decision, with no processes and no clock in it, so a proof
    /// can drive every path in a millisecond.
    public static State Next(State current, bool processAlive,
                             TimeSpan sinceLaunch, TimeSpan grace) => current switch
    {
        State.WaitingForGame when processAlive => State.Running,
        State.WaitingForGame when sinceLaunch >= grace => State.NeverAppeared,
        State.WaitingForGame => State.WaitingForGame,

        State.Running when processAlive => State.Running,
        State.Running => State.Exited,

        _ => current,      // Exited and NeverAppeared are final
    };

    private readonly string _gameFolder;
    private readonly Action<string> _log;
    private readonly Action<int> _onExit;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _poll;

    private CancellationTokenSource? _cts;
    private int _fired;                    // GameExited is raised exactly once

    public State Current { get; private set; } = State.WaitingForGame;

    public GameWatcher(string gameFolder, Action<string> log, Action<int> onExit,
                       TimeSpan? grace = null, TimeSpan? poll = null)
    {
        _gameFolder = gameFolder;
        _log = log;
        _onExit = onExit;
        // Steam has to start, maybe update, and Unity games often show a
        // configuration dialog first. Two minutes is generous on purpose:
        // the cost of waiting is nothing, and the cost of giving up early is
        // a launcher that stops tracking a game that was merely slow.
        _grace = grace ?? TimeSpan.FromMinutes(2);
        _poll = poll ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = WatchAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task WatchAsync(CancellationToken ct)
    {
        var names = ExecutableNames(_gameFolder);
        if (names.Count == 0)
        {
            _log("No executable found in the game folder, so London cannot tell "
               + "when the game closes — press Stop when you are done.");
            Current = State.NeverAppeared;
            return;
        }

        DateTime launched = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            bool alive = IsAnyAlive(names, _gameFolder);
            State before = Current;
            Current = Next(Current, alive, DateTime.UtcNow - launched, _grace);

            if (Current != before)
            {
                switch (Current)
                {
                    case State.Running:
                        _log("The game is running.");
                        break;
                    case State.Exited:
                        _log("The game has closed.");
                        if (Interlocked.Exchange(ref _fired, 1) == 0) _onExit(0);
                        return;
                    case State.NeverAppeared:
                        // Honest, and actionable: the session stays up, but the
                        // player now knows Stop is theirs to press.
                        _log("London never saw the game start, so it cannot tell when "
                           + "you close it — use Stop here when you are finished.");
                        return;
                }
            }

            try { await Task.Delay(_poll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// Executable names to look for, taken from the game's own folder.
    ///
    /// Two levels deep: plenty of games put the real binary in Binaries\Win64
    /// or similar, and going deeper costs time on a folder with thousands of
    /// asset files for no gain.
    public static IReadOnlyCollection<string> ExecutableNames(string gameFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            return names;
        try
        {
            foreach (string f in Directory.EnumerateFiles(gameFolder, "*.exe"))
                Add(names, f);
            foreach (string dir in Directory.EnumerateDirectories(gameFolder))
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*.exe"))
                        Add(names, f);
                }
                catch { /* a folder we may not read is a folder we skip */ }
            }
        }
        catch { }
        return names;
    }

    /// Helper programs that live beside a game but are not the game. Watching
    /// these would report "running" for a crash reporter that outlives it.
    private static readonly string[] NotTheGame =
    {
        "unitycrashhandler32", "unitycrashhandler64", "crashreportclient",
        "crashpad_handler", "vcredist", "dxsetup", "dotnetfx", "uninstall",
        "eosbootstrapper",
    };

    private static void Add(HashSet<string> names, string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        if (n.Length == 0) return;
        if (NotTheGame.Any(x => n.Equals(x, StringComparison.OrdinalIgnoreCase))) return;
        names.Add(n);
    }

    /// Is one of those executables running, out of THIS folder?
    private static bool IsAnyAlive(IReadOnlyCollection<string> names, string gameFolder)
    {
        foreach (string name in names)
        {
            Process[] hits;
            try { hits = Process.GetProcessesByName(name); }
            catch { continue; }

            try
            {
                foreach (var p in hits)
                {
                    string? image = null;
                    try { image = p.MainModule?.FileName; }
                    catch { /* another user's process, or a bitness we cannot open */ }

                    // Path known: it must be under the game's folder.
                    // Path unknown: the name came from that folder in the first
                    // place, so a match is still the best evidence available —
                    // and erring toward "running" only delays an exit notice,
                    // while erring the other way would end a live session.
                    if (image == null
                        || image.StartsWith(EnsureSlash(gameFolder), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            finally
            {
                foreach (var p in hits) { try { p.Dispose(); } catch { } }
            }
        }
        return false;
    }

    private static string EnsureSlash(string dir)
        => dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
}
