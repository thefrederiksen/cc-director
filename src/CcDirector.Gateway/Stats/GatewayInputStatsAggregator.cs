using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable, always-available aggregate of the DevThrottle Stats input tally. Every session's
/// per-session tally (submitted turns + character volume by modality and surface) rides up the existing
/// director-stream snapshot/delta path on <see cref="SessionDto.InputStats"/>; this aggregator folds them
/// into all-time totals the private Gateway dashboard reads with no cloud round-trip.
///
/// Correct across BOTH a Director restart and a Gateway restart, with no double-counting, via a high-water
/// increment: for each live session it remembers the last per-bucket counts it saw, and adds only the
/// increase to the totals. A session that ends (RemoveSession) leaves its contribution in the totals and
/// its high-water entry is pruned. A session whose reported counts DROP (a Director restarted and the
/// session began a fresh tally) is treated as new activity from zero. Both the totals and the high-water
/// map are persisted (atomic temp-write + rename, corrupt file quarantined) so a Gateway restart neither
/// loses the totals nor re-adds live sessions' current counts.
///
/// Only counts ever pass through here - never the text of anything typed or said (mission decision 5).
/// </summary>
public sealed class GatewayInputStatsAggregator
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    // All-time totals, keyed by (modality token, surface token).
    private readonly Dictionary<(string Modality, string Surface), Counters> _totals = new();

    // Per-live-session last-seen counts, so only the INCREASE is folded into the totals.
    private readonly Dictionary<string, Dictionary<(string Modality, string Surface), Counters>> _highWater = new();

    private sealed class Counters
    {
        public long Turns { get; set; }
        public long Characters { get; set; }
    }

    /// <param name="path">The durable store file. Defaults to gateway-input-stats.json under the cc-director
    /// storage root, beside the other Gateway stores.</param>
    public GatewayInputStatsAggregator(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "gateway-input-stats.json")
            : path!;
        Load();
    }

    /// <summary>Fold every session in a full snapshot into the totals.</summary>
    public void ObserveSnapshot(IEnumerable<SessionDto>? sessions)
    {
        if (sessions is null) return;
        lock (_lock)
        {
            var changed = false;
            foreach (var s in sessions)
                changed |= FoldLocked(s);
            if (changed) Save();
        }
    }

    /// <summary>Fold one session (a delta) into the totals.</summary>
    public void Observe(SessionDto? session)
    {
        if (session is null) return;
        lock (_lock)
        {
            if (FoldLocked(session)) Save();
        }
    }

    /// <summary>
    /// Forget a removed session's high-water entry. Its contribution stays in the totals (it was folded in
    /// as it happened); dropping the high-water entry just stops the map growing without bound.
    /// </summary>
    public void Forget(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock)
        {
            if (_highWater.Remove(sessionId)) Save();
        }
    }

    /// <summary>An immutable snapshot of the all-time totals for the dashboard, buckets in a stable order.</summary>
    public InputStatsDto CurrentTotals()
    {
        lock (_lock)
        {
            return ToDtoLocked(_totals);
        }
    }

    // Fold one session's tally into the totals via the high-water increment. Returns true when the totals
    // changed. Caller holds the lock.
    private bool FoldLocked(SessionDto s)
    {
        if (string.IsNullOrEmpty(s.SessionId) || s.InputStats?.Buckets is null || s.InputStats.Buckets.Count == 0)
            return false;

        if (!_highWater.TryGetValue(s.SessionId, out var hw))
        {
            hw = new Dictionary<(string, string), Counters>();
            _highWater[s.SessionId] = hw;
        }

        var changed = false;
        foreach (var b in s.InputStats.Buckets)
        {
            var key = (b.Modality ?? "", b.Surface ?? "");
            hw.TryGetValue(key, out var prev);
            var prevTurns = prev?.Turns ?? 0;
            var prevChars = prev?.Characters ?? 0;

            // Normal case: counts only grow, so add the increase. Reset case (a Director restarted this
            // session id with a fresh tally): the reported count is LOWER than last seen, so the whole
            // current count is new activity from zero.
            var deltaTurns = b.Turns >= prevTurns ? b.Turns - prevTurns : b.Turns;
            var deltaChars = b.Characters >= prevChars ? b.Characters - prevChars : b.Characters;

            if (deltaTurns > 0 || deltaChars > 0)
            {
                if (!_totals.TryGetValue(key, out var total))
                {
                    total = new Counters();
                    _totals[key] = total;
                }
                total.Turns += deltaTurns;
                total.Characters += deltaChars;
                changed = true;
            }

            hw[key] = new Counters { Turns = b.Turns, Characters = b.Characters };
        }
        return changed;
    }

    private static InputStatsDto ToDtoLocked(Dictionary<(string Modality, string Surface), Counters> src)
    {
        var dto = new InputStatsDto();
        foreach (var kvp in src.OrderBy(k => k.Key.Modality, StringComparer.Ordinal).ThenBy(k => k.Key.Surface, StringComparer.Ordinal))
        {
            dto.Buckets.Add(new InputStatBucketDto
            {
                Modality = kvp.Key.Modality,
                Surface = kvp.Key.Surface,
                Turns = kvp.Value.Turns,
                Characters = kvp.Value.Characters,
            });
        }
        return dto;
    }

    private sealed class StoreFile
    {
        public List<InputStatBucketDto> Totals { get; set; } = new();
        public Dictionary<string, List<InputStatBucketDto>> HighWater { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[GatewayInputStatsAggregator] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }
        if (parsed is null)
        {
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        foreach (var b in parsed.Totals)
            _totals[(b.Modality ?? "", b.Surface ?? "")] = new Counters { Turns = b.Turns, Characters = b.Characters };
        foreach (var (sid, buckets) in parsed.HighWater)
        {
            var hw = new Dictionary<(string, string), Counters>();
            foreach (var b in buckets)
                hw[(b.Modality ?? "", b.Surface ?? "")] = new Counters { Turns = b.Turns, Characters = b.Characters };
            _highWater[sid] = hw;
        }
        FileLog.Write($"[GatewayInputStatsAggregator] Load: restored {_totals.Count} total bucket(s), {_highWater.Count} live session(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[GatewayInputStatsAggregator] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    // Write-through under the lock: serialize the whole store and atomically replace the file (temp +
    // rename) so a concurrent reader or a crash mid-write never sees a half-written store. A failed save is
    // a LOGGED error that propagates - never a silent skip.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Totals = ToDtoLocked(_totals).Buckets };
            foreach (var (sid, hw) in _highWater)
                file.HighWater[sid] = ToDtoLocked(hw).Buckets;

            var json = JsonSerializer.Serialize(file, FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayInputStatsAggregator] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
