using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using LauncherV2.Core;

namespace LauncherV2.Plugins.Catalog;

// London's side of a game-mod bridge: newline-delimited JSON over localhost.
//
// This is the same relay job the Diablo II plugin does over a pipe and the
// console plugins do over their emulator bridges — London holds the one
// Archipelago connection, and the game's mod holds none. The mod dials this
// port; whoever answers is its counterpart. For a player without London that
// counterpart is the world's own text client, and the protocol is identical,
// which is exactly why the mod never needs to know the difference.
//
// Wire format, both directions: one JSON object per line, UTF-8.
//   in  from the mod: Hello, Check {locations:[names]}, Goal, Death, Log
//   out to the mod:   Handshake {slot, seed, slot_data}, Item {index,id,name,from}, DeathLink
//
// ⚠ The Handshake's `slot` is the SLOT NUMBER and `seed` the seed name —
//   the mod builds its save key from the pair. The world's own client sends
//   exactly this shape, and London must match it, or a player who switches
//   between the two forks their progress into two save files.
public sealed class ModRelay : IDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // The connected mod, guarded by _gate. One at a time: a second game
    // process dialing in replaces the first, because the newest game is the
    // one the player is looking at.
    private readonly object _gate = new();
    private StreamWriter? _writer;

    // --- session facts, set as London learns them --------------------------
    private int _slot = -1;
    private string? _seedName;
    private JsonElement? _slotData;

    private Dictionary<long, string>? _itemNames;    // id  -> name
    private IReadOnlyDictionary<string, long>? _locationIds; // name -> id

    // The full item history in server order. AP replays from index 0 on every
    // connect, and the mod dedups on index, so resending is always safe and
    // never double-grants.
    private readonly List<(long Id, string From)> _items = new();
    private int _sentCount;
    private bool _handshakeSent;
    private bool _handshakeHadData;

    // Checks the mod named before London had the location table. Parked, not
    // dropped: a check that goes missing here is a seed that cannot finish.
    private readonly List<string> _parkedChecks = new();

    public event Action<long[]>? ChecksResolved;
    public event Action? GoalReached;
    public event Action<string>? DeathReported;

    public ModRelay(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    public bool ModConnected { get { lock (_gate) return _writer != null; } }

    // --- lifecycle ----------------------------------------------------------

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _log($"Waiting for the game's mod on 127.0.0.1:{_port} …");
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _handshakeSent = false;
                _handshakeHadData = false;
            _sentCount = 0;
        }
    }

    public void Dispose() => Stop();

    // --- what London tells the relay ----------------------------------------

    public void SetSession(int slot, string? seedName)
    {
        lock (_gate) { _slot = slot; _seedName = seedName; }
        Flush();
    }

    public void SetSlotData(JsonElement slotData)
    {
        // Clone: the element's backing document is disposed by the caller.
        lock (_gate) _slotData = slotData.Clone();
        Flush();
    }

    public void SetItemTable(IReadOnlyDictionary<string, long> nameToId)
    {
        var byId = new Dictionary<long, string>(nameToId.Count);
        foreach (var kv in nameToId) byId[kv.Value] = kv.Key;
        lock (_gate) _itemNames = byId;
        Flush();
    }

    public void SetLocationTable(IReadOnlyDictionary<string, long> nameToId)
    {
        string[] parked;
        lock (_gate)
        {
            _locationIds = nameToId;
            parked = _parkedChecks.ToArray();
            _parkedChecks.Clear();
        }
        if (parked.Length > 0) ResolveChecks(parked);
    }

    /// Items from the server, placed at their absolute indices. AP's resume
    /// contract replays the full history from 0, so overlaps are normal and
    /// the list is the union of everything ever seen.
    public void PutItems(ApNetworkItem[] items, int index, Func<int, string> playerName)
    {
        lock (_gate)
        {
            for (int i = 0; i < items.Length; i++)
            {
                int at = index + i;
                var entry = (items[i].ItemId, playerName(items[i].Player));
                if (at < _items.Count) _items[at] = entry;
                else if (at == _items.Count) _items.Add(entry);
                else
                {
                    // A gap means we missed a packet; deliver nothing past it
                    // rather than granting items under the wrong indices —
                    // the next full replay (reconnect) fills the hole.
                    _log($"Item index gap: expected {_items.Count}, got {at}. "
                       + "Holding delivery until the server replays.");
                    return;
                }
            }
        }
        Flush();
    }

    public void SendDeathLink(string source, string cause)
        => WriteLine(JsonSerializer.Serialize(new { cmd = "DeathLink", source, cause }));

    // --- the wire -------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { _log($"Mod listener error: {e.Message}"); return; }

            lock (_gate)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                // A fresh connection knows nothing: full handshake and replay.
                _handshakeSent = false;
                _handshakeHadData = false;
                _sentCount = 0;
            }
            _ = ReadLoopAsync(client, ct);
            Flush();
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                HandleLine(line);
            }
        }
        catch (Exception) { /* the disconnect below is the message */ }
        finally
        {
            lock (_gate)
            {
                if (_writer != null) { try { _writer.Dispose(); } catch { } }
                _writer = null;
                _handshakeSent = false;
                _handshakeHadData = false;
                _sentCount = 0;
            }
            _log("The game's mod disconnected.");
        }
    }

    private void HandleLine(string line)
    {
        string cmd;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        }
        catch (JsonException) { return; }

        switch (cmd)
        {
            case "Hello":
                _log($"Game mod connected (mod {Str(root, "mod_version")}, game {Str(root, "game_version")}).");
                Flush();
                break;

            case "Check":
                if (root.TryGetProperty("locations", out var locs)
                    && locs.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var v in locs.EnumerateArray())
                        if (v.GetString() is { Length: > 0 } n) names.Add(n);
                    ResolveChecks(names.ToArray());
                }
                break;

            case "Goal":
                GoalReached?.Invoke();
                break;

            case "Death":
                DeathReported?.Invoke(Str(root, "cause") ?? "");
                break;

            case "Log":
                _log($"[game] {Str(root, "text")}");
                break;
        }
    }

    private void ResolveChecks(string[] names)
    {
        var ids = new List<long>(names.Length);
        lock (_gate)
        {
            if (_locationIds == null)
            {
                // Too early: London has not received the DataPackage yet.
                _parkedChecks.AddRange(names);
                return;
            }
            foreach (string n in names)
            {
                if (_locationIds.TryGetValue(n, out long id)) ids.Add(id);
                else
                    // Reported, never swallowed: an unknown name means the mod
                    // and the apworld disagree about this seed, and silence
                    // here becomes an unfinishable world later.
                    _log($"The mod sent an unknown location: \"{n}\"");
            }
        }
        if (ids.Count > 0) ChecksResolved?.Invoke(ids.ToArray());
    }

    /// Send everything the mod is now entitled to. Called after every state
    /// change; each pass sends only what the previous passes could not.
    private void Flush()
    {
        lock (_gate)
        {
            if (_writer == null) return;

            // The handshake goes out as soon as there is a session, and again
            // if the seed's slot_data turns up afterwards.
            //
            // ⚠ Order is not guaranteed. A game already running when the
            // player joins dials us before Archipelago has sent slot_data, and
            // a handshake without it configures the mod from defaults: wrong
            // carrier counts, wrong goal, checks sent for locations the seed
            // does not have. Sending once and never again made that permanent
            // for the whole session.
            bool haveData = _slotData is not null;
            if (!_handshakeSent || (!_handshakeHadData && haveData))
            {
                if (_slot < 0) return;   // no session yet — nothing to say
                var payload = new Dictionary<string, object?>
                {
                    ["cmd"] = "Handshake",
                    ["slot"] = _slot,
                    ["seed"] = _seedName ?? "unknown",
                };
                if (_slotData is { } sd) payload["slot_data"] = sd;
                if (!TryWriteLocked(JsonSerializer.Serialize(payload))) return;
                _handshakeSent = true;
                _handshakeHadData = haveData;
            }

            if (_itemNames == null && _sentCount < _items.Count)
                return;  // names not known yet; the table always arrives

            while (_sentCount < _items.Count)
            {
                var (id, from) = _items[_sentCount];
                if (!_itemNames!.TryGetValue(id, out string? name))
                {
                    // An id with no name in this seed's own table is a version
                    // mismatch, not a delivery problem. Skipping would shift
                    // every later index, so stop and say why.
                    _log($"Item id {id} has no name in this seed's table — "
                       + "the apworld and the multiworld disagree. Holding delivery.");
                    return;
                }
                string msg = JsonSerializer.Serialize(new
                {
                    cmd = "Item",
                    index = _sentCount,
                    id,
                    name,
                    from,
                });
                if (!TryWriteLocked(msg)) return;
                _sentCount++;
            }
        }
    }

    private void WriteLine(string line)
    {
        lock (_gate) TryWriteLocked(line);
    }

    /// Caller holds _gate. False means the mod is gone; the reconnect replays.
    private bool TryWriteLocked(string line)
    {
        if (_writer == null) return false;
        try { _writer.WriteLine(line); return true; }
        catch (Exception)
        {
            try { _writer.Dispose(); } catch { }
            _writer = null;
            _handshakeSent = false;
                _handshakeHadData = false;
            _sentCount = 0;
            return false;
        }
    }

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
}
