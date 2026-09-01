// Mission Control — London's own window over StarCraft 2's mission board.
//
// The AP client's kivy launcher tab shows 83 unlabelled colour blocks. This
// window shows the same board with the information the session already holds:
// checks per mission, progress, why a mission is locked — and a launch button
// that drives the world's own machinery through the headless bridge
// (sc2_bridge.apworld), so the kivy window never has to open.
//
// Data sources, all already flowing into the plugin:
//   * slot_data.custom_mission_order — campaigns → layouts → columns → missions
//   * the location table (name → id): every check is named
//     "<mission FullName>: <objective>", so per-mission counts are a prefix walk
//   * checked location ids, live from the server
//
// Availability is evaluated from the entry rules in slot_data (beat-count
// rules); anything the evaluator does not recognise is left "open", because
// the world's own play_mission validates for real on launch.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Catalog;

// ---------------------------------------------------------------- the model

internal sealed class Sc2Mission
{
    public int Id;
    public string Name = "";
    public string FullName = "";
    public string Race = "";
    public JsonElement EntryRule;
    public long VictoryLoc = -1;
    public List<(long Id, string Objective)> Locations = new();

    public int Done(HashSet<long> got) => Locations.Count(l => got.Contains(l.Id));
    public bool Beaten(HashSet<long> got) => VictoryLoc >= 0 && got.Contains(VictoryLoc);
}

internal sealed class Sc2Column
{
    public string Title = "";
    public List<Sc2Mission> Missions = new();
}

internal sealed class Sc2Board
{
    public List<Sc2Column> Columns = new();
    public Dictionary<int, Sc2Mission> ById = new();

    /// Parse slot_data + the location table into a drawable board.
    public static Sc2Board? Build(JsonElement? slotData,
                                  IReadOnlyDictionary<string, long>? locationTable)
    {
        if (slotData is not { } sd) return null;
        if (!sd.TryGetProperty("custom_mission_order", out var cmo)
            || cmo.ValueKind != JsonValueKind.Array) return null;

        var board = new Sc2Board();
        foreach (var campaign in cmo.EnumerateArray())
        {
            if (!campaign.TryGetProperty("layouts", out var layouts)) continue;
            foreach (var layout in layouts.EnumerateArray())
            {
                string lname = layout.TryGetProperty("name", out var ln)
                    ? ln.GetString() ?? "" : "";
                if (!layout.TryGetProperty("missions", out var cols)) continue;
                foreach (var colEl in cols.EnumerateArray())
                {
                    var col = new Sc2Column { Title = lname };
                    foreach (var mEl in colEl.EnumerateArray())
                    {
                        if (!mEl.TryGetProperty("mission_id", out var idEl)) continue;
                        int id = idEl.GetInt32();
                        // The layout pads its grid with empty cells
                        // (mission_id -1) so columns line up — the golden
                        // path's staircase shape. They are spacing, not
                        // missions: kept as gaps, never as cards.
                        if (id < 0)
                        {
                            col.Missions.Add(new Sc2Mission { Id = -1 });
                            continue;
                        }
                        var m = new Sc2Mission { Id = id };
                        if (Sc2MissionTable.ById.TryGetValue(id, out var info))
                        {
                            m.Name = info.Name; m.FullName = info.FullName;
                            m.Race = info.Race;
                        }
                        else { m.Name = m.FullName = $"Mission {id}"; }
                        if (mEl.TryGetProperty("entry_rule", out var rule))
                            m.EntryRule = rule.Clone();
                        col.Missions.Add(m);
                        board.ById[id] = m;
                    }
                    if (col.Missions.Count > 0) board.Columns.Add(col);
                }
            }
        }
        if (board.Columns.Count == 0) return null;

        // Locations by wire-name prefix. Longest-prefix wins, so "The Outlaws"
        // never swallows "The Outlaws (Zerg)".
        if (locationTable != null)
        {
            var byFull = board.ById.Values
                .OrderByDescending(m => m.FullName.Length).ToList();
            foreach (var kv in locationTable)
            {
                foreach (var m in byFull)
                {
                    if (!kv.Key.StartsWith(m.FullName + ": ", StringComparison.Ordinal))
                        continue;
                    string obj = kv.Key[(m.FullName.Length + 2)..];
                    m.Locations.Add((kv.Value, obj));
                    if (obj == "Victory") m.VictoryLoc = kv.Value;
                    break;
                }
            }
            foreach (var m in board.ById.Values)
                m.Locations.Sort((a, b) =>
                    a.Objective == "Victory" ? -1 : b.Objective == "Victory" ? 1
                    : string.Compare(a.Objective, b.Objective, StringComparison.Ordinal));
        }
        return board;
    }

    /// A beat-count entry rule, evaluated. Null = a shape we do not know —
    /// the caller treats that as "open" and lets play_mission be the judge.
    public bool? RuleMet(JsonElement rule, HashSet<long> got)
    {
        if (rule.ValueKind != JsonValueKind.Object) return true;
        int amount = rule.TryGetProperty("amount", out var am) ? am.GetInt32() : 0;

        if (rule.TryGetProperty("mission_ids", out var ids)
            && ids.ValueKind == JsonValueKind.Array)
        {
            int beaten = ids.EnumerateArray()
                .Count(x => ById.TryGetValue(x.GetInt32(), out var m) && m.Beaten(got));
            return beaten >= amount;
        }

        if (rule.TryGetProperty("sub_rules", out var subs)
            && subs.ValueKind == JsonValueKind.Array)
        {
            int met = 0; bool unknown = false;
            foreach (var s in subs.EnumerateArray())
            {
                var r = RuleMet(s, got);
                if (r == null) unknown = true;
                else if (r == true) met++;
            }
            if (met >= amount) return true;
            return unknown ? null : false;
        }
        return null;
    }
}

// --------------------------------------------------------------- the bridge

/// The headless driver: ArchipelagoLauncher's "SC2 London Bridge" component
/// (shipped inside this plugin as sc2_bridge.apworld), spoken to over stdio.
/// Proven end to end: connect → BOARD:95 → STATE:ready → PLAY → clean EXIT.
internal sealed class Sc2Bridge : IDisposable
{
    private Process? _proc;
    public bool Ready { get; private set; }
    public event Action<string>? StateChanged;
    public event Action<string>? LineReceived;

    public static string? FindLauncherExe()
    {
        var st = SettingsStore.Load();
        foreach (string root in new[] { st.ApEnginePath, st.ApworldSyncDir,
                                        @"C:\ProgramData\Archipelago" })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string cand = Path.Combine(root, "ArchipelagoLauncher.exe");
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    /// Put sc2_bridge.apworld into the engine, from the copy embedded in this
    /// plugin. Overwrites on size change so a plugin update carries it.
    public static bool EnsureInstalled(string launcherExe, Action<string>? log)
    {
        try
        {
            string worlds = Path.Combine(Path.GetDirectoryName(launcherExe)!, "custom_worlds");
            Directory.CreateDirectory(worlds);
            string target = Path.Combine(worlds, "sc2_bridge.apworld");

            var asm = typeof(Sc2Bridge).Assembly;
            string? res = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("sc2_bridge.apworld",
                                                StringComparison.OrdinalIgnoreCase));
            if (res == null) { log?.Invoke("bridge apworld resource missing"); return false; }

            using var s = asm.GetManifestResourceStream(res)!;
            if (File.Exists(target) && new FileInfo(target).Length == s.Length)
                return true;
            using var f = File.Create(target);
            s.CopyTo(f);
            log?.Invoke("installed sc2_bridge.apworld into the engine");
            return true;
        }
        catch (Exception e) { log?.Invoke("bridge install failed: " + e.Message); return false; }
    }

    public bool Start(string launcherExe, string auth, string server, string? sc2Path)
    {
        Stop();
        Ready = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = launcherExe,
                Arguments = "\"SC2 London Bridge\" -- --connect "
                          + $"\"archipelago://{auth}@{server}\"",
                WorkingDirectory = Path.GetDirectoryName(launcherExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            if (sc2Path != null) psi.EnvironmentVariables["SC2PATH"] = sc2Path;

            _proc = Process.Start(psi);
            if (_proc == null) return false;
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            { Ready = false; StateChanged?.Invoke("engine closed"); };
            _proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                LineReceived?.Invoke(e.Data);
                if (e.Data.StartsWith("STATE:ready")) { Ready = true; StateChanged?.Invoke("ready"); }
                else if (e.Data.StartsWith("STATE:")) StateChanged?.Invoke(e.Data[6..]);
            };
            _proc.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) LineReceived?.Invoke("err: " + e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            StateChanged?.Invoke("starting engine…");
            return true;
        }
        catch (Exception e) { StateChanged?.Invoke("engine failed: " + e.Message); return false; }
    }

    public void Play(int missionId)
    {
        try { _proc?.StandardInput.WriteLine($"PLAY {missionId}"); }
        catch (Exception) { StateChanged?.Invoke("engine pipe lost"); }
    }

    public void Stop()
    {
        var p = _proc; _proc = null; Ready = false;
        if (p == null) return;
        try
        {
            try { p.StandardInput.WriteLine("EXIT"); } catch (Exception) { }
            if (!p.WaitForExit(4000)) p.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        finally { p.Dispose(); }
    }

    public void Dispose() => Stop();
}

// --------------------------------------------------------------- the window

internal sealed class Sc2MissionWindow : Window
{
    // London's palette, hex for hex.
    private static readonly Brush Bg      = Hex("#0D1018");
    private static readonly Brush Panel   = Hex("#141824");
    private static readonly Brush Card    = Hex("#1B2030");
    private static readonly Brush CardHi  = Hex("#222941");
    private static readonly Brush Line    = Hex("#262C3E");
    private static readonly Brush Text    = Hex("#E8EAF2");
    private static readonly Brush Muted   = Hex("#7A83A0");
    private static readonly Brush Dim     = Hex("#4C5470");
    private static readonly Brush Gold    = Hex("#E5B617");
    private static readonly Brush GoldInk = Hex("#151004");
    private static readonly Brush Green   = Hex("#4FA97B");
    private static readonly Brush Blue    = Hex("#5B7BD5");
    private static readonly Brush Locked  = Hex("#343B52");
    private static Brush Hex(string s) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));

    private readonly Func<(JsonElement? SlotData, IReadOnlyDictionary<string, long>? Locs,
                           HashSet<long> Checked)> _data;
    private readonly Func<Sc2Bridge?> _bridge;
    private readonly Func<bool> _startBridge;

    private Sc2Board? _board;
    private Sc2Mission? _selected;
    private string _filter = "all";

    private readonly StackPanel _columnsHost = new()
    { Orientation = Orientation.Horizontal };
    private readonly StackPanel _detailHost = new();
    private readonly TextBlock _statChecks = Stat();
    private readonly TextBlock _statBeaten = Stat();
    private readonly TextBlock _footer = new()
    { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Stat() => new()
    { FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Text };

    public Sc2MissionWindow(
        string slotName, string address,
        Func<(JsonElement?, IReadOnlyDictionary<string, long>?, HashSet<long>)> data,
        Func<Sc2Bridge?> bridge, Func<bool> startBridge)
    {
        _data = data; _bridge = bridge; _startBridge = startBridge;

        Title = "StarCraft II — Mission Control";
        Width = 1120; Height = 720;
        MinWidth = 760; MinHeight = 480;
        Background = Bg;

        var root = new DockPanel();

        // top strip
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Panel,
            Margin = new Thickness(0),
        };
        var title = new StackPanel { Margin = new Thickness(18, 12, 26, 12) };
        title.Children.Add(new TextBlock
        {
            Text = "MISSION CONTROL", FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = Text,
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{slotName} · {address}", FontSize = 11.5, Foreground = Muted,
        });
        top.Children.Add(title);
        top.Children.Add(StatBlock("CHECKS", _statChecks));
        top.Children.Add(StatBlock("MISSIONS BEATEN", _statBeaten));
        foreach (var (key, label) in new[]
                 { ("all", "All"), ("avail", "Available"),
                   ("open", "Has checks"), ("done", "Beaten") })
        {
            var b = new Button
            {
                Content = label, FontSize = 11.5, Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 4, 0), Background = Panel,
                Foreground = Muted, BorderBrush = Line, Tag = key,
                VerticalAlignment = VerticalAlignment.Center,
            };
            b.Click += (_, _) => { _filter = key; Redraw(); };
            top.Children.Add(b);
        }
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // footer
        var foot = new Border
        {
            Background = Hex("#0A0D14"), BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 7, 16, 7), Child = _footer,
        };
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        _footer.Text = "Engine idle — launching a mission starts it.";

        // detail panel
        var detail = new Border
        {
            Width = 300, Background = Card, BorderBrush = Line,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(16),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _detailHost,
            },
        };
        DockPanel.SetDock(detail, Dock.Right);
        root.Children.Add(detail);

        // board
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Panel,
            Padding = new Thickness(14),
            Content = _columnsHost,
        });

        Content = root;
        Refresh();
    }

    private static UIElement StatBlock(string label, TextBlock value)
    {
        var s = new StackPanel { Margin = new Thickness(0, 10, 26, 10), MinWidth = 96 };
        s.Children.Add(value);
        s.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10, Foreground = Muted,
            FontWeight = FontWeights.SemiBold,
        });
        return s;
    }

    /// Rebuild the model from live data and redraw. Called on open and on
    /// every session change (checks landing, table arriving).
    public void Refresh()
    {
        var (sd, locs, got) = _data();
        _board = Sc2Board.Build(sd, locs);
        Redraw();
    }

    public void SetFooter(string text) => _footer.Text = text;

    private void Redraw()
    {
        _columnsHost.Children.Clear();
        if (_board == null)
        {
            _columnsHost.Children.Add(new TextBlock
            {
                Text = "Waiting for the session's mission data…",
                Foreground = Muted, FontSize = 13, Margin = new Thickness(10),
            });
            return;
        }
        var (_, _, got) = _data();

        int total = _board.ById.Values.Sum(m => m.Locations.Count);
        int done = _board.ById.Values.Sum(m => m.Done(got));
        int beaten = _board.ById.Values.Count(m => m.Beaten(got));
        _statChecks.Text = $"{done} / {total}";
        _statBeaten.Text = $"{beaten} / {_board.ById.Count}";

        foreach (var col in _board.Columns)
        {
            var stack = new StackPanel { Width = 172, Margin = new Thickness(0, 0, 12, 0) };
            var real = col.Missions.Where(m => m.Id >= 0).ToList();
            int cDone = real.Count(m => m.Beaten(got));
            stack.Children.Add(new TextBlock
            {
                Text = $"{col.Title.ToUpperInvariant()}   {cDone}/{real.Count}",
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted,
                Margin = new Thickness(2, 0, 0, 8), TextAlignment = TextAlignment.Center,
            });
            foreach (var m in col.Missions)
            {
                var el = MissionCard(m, got);
                if (el != null) stack.Children.Add(el);
            }
            _columnsHost.Children.Add(stack);
        }
        RedrawDetail(got);
    }

    private UIElement? MissionCard(Sc2Mission m, HashSet<long> got)
    {
        // A grid gap: air with a card's height, so the staircase shape the
        // layout drew the board in survives. Filtered views are lists, not
        // grids, so the air collapses there.
        if (m.Id < 0)
            return _filter == "all"
                ? new Border { Height = 56, Margin = new Thickness(0, 0, 0, 7) }
                : null;

        bool beaten = m.Beaten(got);
        bool? open = beaten ? true : _board!.RuleMet(m.EntryRule, got);
        int done = m.Done(got), tot = m.Locations.Count;

        string state = beaten ? "done" : open != false ? "avail" : "lock";
        if (_filter == "avail" && state != "avail") return null;
        if (_filter == "done" && state != "done") return null;
        if (_filter == "open" && (state == "lock" || done >= tot)) return null;

        var stripe = beaten ? Green : open != false ? Gold : Locked;
        var card = new Border
        {
            Background = state == "avail" ? CardHi : Card,
            BorderBrush = state == "avail" ? Hex("#8A6F12") : Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 7),
            Padding = new Thickness(0),
            Opacity = state == "lock" ? 0.45 : beaten ? 0.8 : 1.0,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = m,
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border { Background = stripe });

        var body = new StackPanel { Margin = new Thickness(9, 7, 9, 7) };
        Grid.SetColumn(body, 1);
        body.Children.Add(new TextBlock
        {
            Text = (state == "lock" ? "🔒 " : "") + m.Name + (beaten ? " ✓" : ""),
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Text,
            TextWrapping = TextWrapping.Wrap,
        });
        var meta = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var chk = new TextBlock
        { Text = $"{done}/{tot}", FontSize = 11, Foreground = Muted };
        DockPanel.SetDock(chk, Dock.Right);
        meta.Children.Add(chk);
        meta.Children.Add(new TextBlock
        {
            Text = m.Race.ToUpperInvariant(), FontSize = 9.5,
            Foreground = m.Race switch
            {
                "Terran" => Hex("#6FA8DC"), "Zerg" => Hex("#C07F45"),
                "Protoss" => Hex("#9D8CD8"), _ => Dim,
            },
            FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(meta);

        var barBg = new Border
        {
            Height = 3, Background = Line, CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 5, 0, 0),
        };
        body.Children.Add(barBg);
        if (tot > 0)
            barBg.Child = new Border
            {
                Background = stripe, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(0, 148.0 * done / tot),
            };

        g.Children.Add(body);
        card.Child = g;
        card.MouseLeftButtonUp += (_, _) => { _selected = m; Redraw(); };
        if (_selected?.Id == m.Id)
        {
            card.BorderBrush = Gold; card.BorderThickness = new Thickness(2);
        }
        return card;
    }

    private void RedrawDetail(HashSet<long> got)
    {
        _detailHost.Children.Clear();
        var m = _selected;
        if (m == null)
        {
            _detailHost.Children.Add(new TextBlock
            {
                Text = "Select a mission to see its objectives.",
                Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        bool beaten = m.Beaten(got);
        bool? open = beaten ? true : _board!.RuleMet(m.EntryRule, got);

        _detailHost.Children.Add(new TextBlock
        {
            Text = m.Name, FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = Text, TextWrapping = TextWrapping.Wrap,
        });
        _detailHost.Children.Add(new TextBlock
        {
            Text = (Sc2MissionTable.ById.TryGetValue(m.Id, out var i)
                       ? $"{i.Campaign} · {i.Area}" : "")
                 + (m.Race.Length > 0 ? $" · {m.Race}" : ""),
            FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 2, 0, 12),
        });

        _detailHost.Children.Add(new TextBlock
        {
            Text = $"OBJECTIVES · {m.Done(got)} OF {m.Locations.Count} CHECKED",
            FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 7),
        });
        foreach (var (id, obj) in m.Locations)
        {
            bool hit = got.Contains(id);
            var row = new Border
            {
                Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 0, 5),
            };
            var dp = new DockPanel();
            var mark = new TextBlock
            {
                Text = hit ? "✓" : "◆", FontWeight = FontWeights.Bold,
                Foreground = hit ? Green : Gold, Margin = new Thickness(0, 0, 8, 0),
            };
            dp.Children.Add(mark);
            dp.Children.Add(new TextBlock
            {
                Text = obj, FontSize = 12,
                Foreground = hit ? Muted : Text, TextWrapping = TextWrapping.Wrap,
            });
            row.Child = dp;
            _detailHost.Children.Add(row);
        }

        _detailHost.Children.Add(new TextBlock
        {
            Text = beaten ? "Beaten — its remaining checks can still be collected."
                 : open == true ? "Unlocked — entry requirements met."
                 : open == false ? "Locked — beat more missions on its path first."
                 : "Requirements unknown — the game itself decides on launch.",
            FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 12),
        });

        var launch = new Button
        {
            Content = "▶   LAUNCH MISSION",
            FontSize = 14, FontWeight = FontWeights.Bold,
            Background = open == false ? Locked : Gold,
            Foreground = open == false ? Muted : GoldInk,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 11, 0, 11),
            IsEnabled = open != false,
        };
        launch.Click += (_, _) => Launch(m);
        _detailHost.Children.Add(launch);
    }

    private void Launch(Sc2Mission m)
    {
        var b = _bridge();
        if (b is not { Ready: true })
        {
            SetFooter("Starting the mission engine…");
            if (!_startBridge())
            {
                SetFooter("The mission engine could not start — see the session log.");
                return;
            }
            b = _bridge();
            if (b == null) return;
            // Fire once it reports ready; a second click is never needed.
            void OnState(string s)
            {
                if (s != "ready") return;
                b.StateChanged -= OnState;
                Dispatcher.Invoke(() => { SetFooter($"Launching {m.Name}…"); b.Play(m.Id); });
            }
            b.StateChanged += OnState;
            return;
        }
        SetFooter($"Launching {m.Name}…");
        b.Play(m.Id);
    }
}
