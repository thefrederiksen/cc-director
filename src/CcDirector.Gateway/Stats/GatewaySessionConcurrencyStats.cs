using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable record of fleet CONCURRENCY and an hourly activity log. Unlike the input tally, a
/// session count needs no per-Director instrumentation: the Gateway's assembled /sessions roster already
/// sees every session on every machine (new build or old), so this is fleet-wide from day one. Observed
/// from that same assembled roster on every read.
///
/// Two headline series track the peak "how many at once":
///   - LIVE: sessions loaded/running (non-exited) - parallel capacity in flight.
///   - WORKING: sessions whose agent is processing a turn right now (activityState = Working).
/// Each keeps the current value and the all-time peak (+when).
///
/// An hourly log (keyed by UTC clock hour "yyyy-MM-ddTHH") records, for every hour:
///   - the max concurrent LIVE and WORKING seen that hour (the "24-hour chart" series),
///   - how many DISTINCT sessions, machines, and repositories were seen that hour (the "how much ran"
///     totals). Distinct counts dedupe across the hour's observations via a current-hour id set that is
///     persisted too, so a Gateway restart mid-hour keeps counting the same hour correctly.
/// Weekly max (or any window) is DERIVED from the hourly log. Persisted (atomic temp-write + rename,
/// corrupt file quarantined); hourly buckets past the retention window are pruned so the store stays
/// bounded.
/// </summary>
public sealed class GatewaySessionConcurrencyStats
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    private const int RetentionDays = 90;
    private const string HourFormat = "yyyy-MM-ddTHH";

    private readonly string _path;
    private readonly object _lock = new();

    private int _liveCurrent;
    private int _workingCurrent;
    private int _liveAllTimeMax;
    private DateTime? _liveAllTimeMaxAtUtc;
    private int _workingAllTimeMax;
    private DateTime? _workingAllTimeMaxAtUtc;

    // Per-hour log, keyed by UTC clock hour.
    private readonly Dictionary<string, HourStat> _hours = new(StringComparer.Ordinal);

    // Dedup sets for the CURRENT hour only, so distinct counts are correct across many observations. Rolled
    // when the clock hour changes; persisted so a restart mid-hour resumes the same hour's dedup.
    private string _currentHourKey = "";
    private readonly HashSet<string> _curSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _curMachines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _curRepos = new(StringComparer.OrdinalIgnoreCase);

    private sealed class HourStat
    {
        public int MaxLive;
        public int MaxWorking;
        public int DistinctSessions;
        public int DistinctMachines;
        public int DistinctRepos;
    }

    /// <param name="path">The durable store file. Defaults to gateway-concurrency-stats.json under the
    /// cc-director storage root, beside the other Gateway stores.</param>
    public GatewaySessionConcurrencyStats(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "gateway-concurrency-stats.json")
            : path!;
        Load();
    }

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Observe the current fleet <paramref name="roster"/> at <paramref name="nowUtc"/>: update the live and
    /// working current values and all-time peaks, and fold this hour's max concurrency plus its distinct
    /// session / machine / repository counts. Idempotent within an hour - a peak or distinct count only ever
    /// grows - so folding on every /sessions read captures the hourly log without inflating anything.
    /// </summary>
    public void Observe(IReadOnlyCollection<SessionDto>? roster, DateTime nowUtc)
    {
        if (roster is null) return;
        var key = HourKey(nowUtc);
        lock (_lock)
        {
            if (key != _currentHourKey)
            {
                _currentHourKey = key;
                _curSessions.Clear();
                _curMachines.Clear();
                _curRepos.Clear();
            }

            var liveCount = 0;
            var workingCount = 0;
            foreach (var s in roster)
            {
                if (string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)) continue;
                liveCount++;
                if (string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)) workingCount++;
                if (!string.IsNullOrEmpty(s.SessionId)) _curSessions.Add(s.SessionId);
                if (!string.IsNullOrWhiteSpace(s.MachineName)) _curMachines.Add(s.MachineName);
                if (!string.IsNullOrWhiteSpace(s.RepoPath)) _curRepos.Add(s.RepoPath);
            }

            _liveCurrent = liveCount;
            _workingCurrent = workingCount;

            var changed = false;
            if (liveCount > _liveAllTimeMax) { _liveAllTimeMax = liveCount; _liveAllTimeMaxAtUtc = nowUtc; changed = true; }
            if (workingCount > _workingAllTimeMax) { _workingAllTimeMax = workingCount; _workingAllTimeMaxAtUtc = nowUtc; changed = true; }

            if (!_hours.TryGetValue(key, out var h))
            {
                h = new HourStat();
                _hours[key] = h;
                changed = true;
            }
            if (liveCount > h.MaxLive) { h.MaxLive = liveCount; changed = true; }
            if (workingCount > h.MaxWorking) { h.MaxWorking = workingCount; changed = true; }
            if (_curSessions.Count > h.DistinctSessions) { h.DistinctSessions = _curSessions.Count; changed = true; }
            if (_curMachines.Count > h.DistinctMachines) { h.DistinctMachines = _curMachines.Count; changed = true; }
            if (_curRepos.Count > h.DistinctRepos) { h.DistinctRepos = _curRepos.Count; changed = true; }

            if (changed)
            {
                PruneLocked(nowUtc);
                Save();
            }
        }
    }

    /// <summary>A read-only snapshot for the dashboard / agent API: the live and working series (current,
    /// all-time peak, derived weekly max) and the full hourly log.</summary>
    public ConcurrencySnapshot Snapshot(DateTime nowUtc)
    {
        lock (_lock)
        {
            var weeklyCutoff = nowUtc.AddDays(-7);
            var liveWeekly = 0;
            var workingWeekly = 0;
            var hourly = new List<ConcurrencyHourDto>(_hours.Count);
            foreach (var kvp in _hours)
            {
                var h = kvp.Value;
                hourly.Add(new ConcurrencyHourDto
                {
                    Hour = kvp.Key,
                    MaxLive = h.MaxLive,
                    MaxWorking = h.MaxWorking,
                    Sessions = h.DistinctSessions,
                    Machines = h.DistinctMachines,
                    Repos = h.DistinctRepos,
                });
                if (TryParseHour(kvp.Key, out var dt) && dt >= weeklyCutoff)
                {
                    if (h.MaxLive > liveWeekly) liveWeekly = h.MaxLive;
                    if (h.MaxWorking > workingWeekly) workingWeekly = h.MaxWorking;
                }
            }
            hourly.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return new ConcurrencySnapshot(
                new ConcurrencySeriesDto { Current = _liveCurrent, AllTimeMax = _liveAllTimeMax, AllTimeMaxAtUtc = _liveAllTimeMaxAtUtc, WeeklyMax = liveWeekly },
                new ConcurrencySeriesDto { Current = _workingCurrent, AllTimeMax = _workingAllTimeMax, AllTimeMaxAtUtc = _workingAllTimeMaxAtUtc, WeeklyMax = workingWeekly },
                hourly);
        }
    }

    private void PruneLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var stale = _hours.Keys.Where(k => TryParseHour(k, out var dt) && dt < cutoff).ToList();
        foreach (var k in stale) _hours.Remove(k);
    }

    private static bool TryParseHour(string key, out DateTime utc) =>
        DateTime.TryParseExact(key, HourFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private sealed class HourStatStore
    {
        public int MaxLive { get; set; }
        public int MaxWorking { get; set; }
        public int DistinctSessions { get; set; }
        public int DistinctMachines { get; set; }
        public int DistinctRepos { get; set; }
    }

    private sealed class StoreFile
    {
        public int LiveAllTimeMax { get; set; }
        public DateTime? LiveAllTimeMaxAtUtc { get; set; }
        public int WorkingAllTimeMax { get; set; }
        public DateTime? WorkingAllTimeMaxAtUtc { get; set; }
        public Dictionary<string, HourStatStore> Hours { get; set; } = new();
        public string CurrentHourKey { get; set; } = "";
        public List<string> CurrentSessions { get; set; } = new();
        public List<string> CurrentMachines { get; set; } = new();
        public List<string> CurrentRepos { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[GatewaySessionConcurrencyStats] Load: no store file at {_path}; starting empty");
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

        _liveAllTimeMax = parsed.LiveAllTimeMax;
        _liveAllTimeMaxAtUtc = parsed.LiveAllTimeMaxAtUtc;
        _workingAllTimeMax = parsed.WorkingAllTimeMax;
        _workingAllTimeMaxAtUtc = parsed.WorkingAllTimeMaxAtUtc;
        foreach (var (hour, hs) in parsed.Hours)
            _hours[hour] = new HourStat
            {
                MaxLive = hs.MaxLive,
                MaxWorking = hs.MaxWorking,
                DistinctSessions = hs.DistinctSessions,
                DistinctMachines = hs.DistinctMachines,
                DistinctRepos = hs.DistinctRepos,
            };
        _currentHourKey = parsed.CurrentHourKey ?? "";
        foreach (var s in parsed.CurrentSessions) _curSessions.Add(s);
        foreach (var m in parsed.CurrentMachines) _curMachines.Add(m);
        foreach (var r in parsed.CurrentRepos) _curRepos.Add(r);
        FileLog.Write($"[GatewaySessionConcurrencyStats] Load: live peak {_liveAllTimeMax}, working peak {_workingAllTimeMax}, {_hours.Count} hourly bucket(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[GatewaySessionConcurrencyStats] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
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

            var file = new StoreFile
            {
                LiveAllTimeMax = _liveAllTimeMax,
                LiveAllTimeMaxAtUtc = _liveAllTimeMaxAtUtc,
                WorkingAllTimeMax = _workingAllTimeMax,
                WorkingAllTimeMaxAtUtc = _workingAllTimeMaxAtUtc,
                CurrentHourKey = _currentHourKey,
                CurrentSessions = _curSessions.ToList(),
                CurrentMachines = _curMachines.ToList(),
                CurrentRepos = _curRepos.ToList(),
            };
            foreach (var (hour, h) in _hours)
                file.Hours[hour] = new HourStatStore
                {
                    MaxLive = h.MaxLive,
                    MaxWorking = h.MaxWorking,
                    DistinctSessions = h.DistinctSessions,
                    DistinctMachines = h.DistinctMachines,
                    DistinctRepos = h.DistinctRepos,
                };

            var json = JsonSerializer.Serialize(file, FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewaySessionConcurrencyStats] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}

/// <summary>One tracked concurrency dimension (live or working): current, all-time peak (+when), and the
/// derived weekly max.</summary>
public sealed class ConcurrencySeriesDto
{
    public int Current { get; set; }
    public int AllTimeMax { get; set; }
    public DateTime? AllTimeMaxAtUtc { get; set; }
    public int WeeklyMax { get; set; }
}

/// <summary>One hour of the fleet activity log (UTC clock hour "yyyy-MM-ddTHH"): the max concurrent live
/// and working counts, and how many distinct sessions, machines, and repositories were seen that hour.</summary>
public sealed class ConcurrencyHourDto
{
    public string Hour { get; set; } = "";
    public int MaxLive { get; set; }
    public int MaxWorking { get; set; }
    public int Sessions { get; set; }
    public int Machines { get; set; }
    public int Repos { get; set; }
}

/// <summary>The concurrency snapshot: both headline series plus the hourly activity log (oldest hour first).</summary>
public sealed record ConcurrencySnapshot(
    ConcurrencySeriesDto Live,
    ConcurrencySeriesDto Working,
    IReadOnlyList<ConcurrencyHourDto> Hourly);
