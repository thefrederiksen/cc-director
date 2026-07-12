using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable record of fleet CONCURRENCY - how many sessions run at once across every machine,
/// and how many are actively working at once. Unlike the input tally, a session COUNT needs no per-Director
/// instrumentation: the Gateway's assembled /sessions roster already sees every session on every machine
/// (new build or old), so this is fleet-wide from day one. Observed from that same assembled roster on
/// every read.
///
/// Two honest series are tracked so a public number is never overstated:
///   - LIVE: sessions loaded/running (non-exited) - the parallel capacity in flight.
///   - WORKING: sessions whose agent is processing a turn right now (activityState = Working) - the count
///     actually churning at an instant, which is smaller and fluctuates.
/// Each series keeps the current value, the all-time peak (and when it happened), and a per-hour max
/// history keyed by UTC clock hour. Weekly max (or any window) is DERIVED from the hourly history - we
/// store hourly once and compute the rest, so there is no separate weekly store to keep consistent.
///
/// Persisted (atomic temp-write + rename, corrupt file quarantined) so a Gateway restart keeps the peaks
/// and the history. Hourly buckets past the retention window are pruned so the store stays bounded.
/// </summary>
public sealed class GatewaySessionConcurrencyStats
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    // ~90 days of hourly buckets: enough for weekly/monthly windows and a long chartable history, bounded
    // so the store never grows without limit.
    private const int RetentionDays = 90;
    private const string HourFormat = "yyyy-MM-ddTHH";

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Series _live = new();
    private readonly Series _working = new();

    // One tracked dimension: current value, all-time peak (+when), and per-hour max history.
    private sealed class Series
    {
        public int Current;
        public int AllTimeMax;
        public DateTime? AllTimeMaxAtUtc;
        public readonly Dictionary<string, int> HourlyMax = new(StringComparer.Ordinal);

        // Fold one observation. Returns true when the persisted state (peak or an hour's max) changed;
        // a bare Current refresh is not persisted on its own.
        public bool Observe(int count, DateTime nowUtc, string hourKey)
        {
            Current = count;
            var changed = false;
            if (count > AllTimeMax)
            {
                AllTimeMax = count;
                AllTimeMaxAtUtc = nowUtc;
                changed = true;
            }
            if (!HourlyMax.TryGetValue(hourKey, out var m) || count > m)
            {
                HourlyMax[hourKey] = count;
                changed = true;
            }
            return changed;
        }
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
    /// Observe the current fleet counts at <paramref name="nowUtc"/>: <paramref name="liveCount"/> is the
    /// non-exited session count, <paramref name="workingCount"/> the subset actively processing a turn.
    /// Idempotent within an hour - only a value higher than this hour's max (or the all-time peak) is
    /// persisted, so folding on every roster read never inflates anything.
    /// </summary>
    public void Observe(int liveCount, int workingCount, DateTime nowUtc)
    {
        if (liveCount < 0) liveCount = 0;
        if (workingCount < 0) workingCount = 0;
        if (workingCount > liveCount) workingCount = liveCount;

        var key = HourKey(nowUtc);
        lock (_lock)
        {
            var changed = _live.Observe(liveCount, nowUtc, key);
            changed |= _working.Observe(workingCount, nowUtc, key);
            if (changed)
            {
                PruneLocked(nowUtc);
                Save();
            }
        }
    }

    /// <summary>A read-only snapshot for the dashboard / agent API: both series with current, all-time peak
    /// (+when), the derived weekly max (max hourly bucket in the last 7 days), and the hourly history.</summary>
    public ConcurrencySnapshot Snapshot(DateTime nowUtc)
    {
        lock (_lock)
        {
            return new ConcurrencySnapshot(SeriesSnapshot(_live, nowUtc), SeriesSnapshot(_working, nowUtc));
        }
    }

    private static ConcurrencySeriesDto SeriesSnapshot(Series s, DateTime nowUtc)
    {
        var weeklyCutoff = nowUtc.AddDays(-7);
        var weeklyMax = 0;
        var hourly = new List<ConcurrencyHourDto>(s.HourlyMax.Count);
        foreach (var kvp in s.HourlyMax)
        {
            if (TryParseHour(kvp.Key, out var dt) && dt >= weeklyCutoff && kvp.Value > weeklyMax)
                weeklyMax = kvp.Value;
            hourly.Add(new ConcurrencyHourDto { Hour = kvp.Key, Max = kvp.Value });
        }
        hourly.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
        return new ConcurrencySeriesDto
        {
            Current = s.Current,
            AllTimeMax = s.AllTimeMax,
            AllTimeMaxAtUtc = s.AllTimeMaxAtUtc,
            WeeklyMax = weeklyMax,
            Hourly = hourly,
        };
    }

    private void PruneLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        Prune(_live, cutoff);
        Prune(_working, cutoff);
    }

    private static void Prune(Series s, DateTime cutoff)
    {
        var stale = s.HourlyMax.Keys.Where(k => TryParseHour(k, out var dt) && dt < cutoff).ToList();
        foreach (var k in stale) s.HourlyMax.Remove(k);
    }

    private static bool TryParseHour(string key, out DateTime utc) =>
        DateTime.TryParseExact(key, HourFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private sealed class SeriesStore
    {
        public int AllTimeMax { get; set; }
        public DateTime? AllTimeMaxAtUtc { get; set; }
        public Dictionary<string, int> HourlyMax { get; set; } = new();
    }

    private sealed class StoreFile
    {
        public SeriesStore Live { get; set; } = new();
        public SeriesStore Working { get; set; } = new();
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

        RestoreSeries(_live, parsed.Live);
        RestoreSeries(_working, parsed.Working);
        FileLog.Write($"[GatewaySessionConcurrencyStats] Load: live peak {_live.AllTimeMax}, working peak {_working.AllTimeMax}, {_live.HourlyMax.Count} live + {_working.HourlyMax.Count} working hourly bucket(s) from {_path}");
    }

    private static void RestoreSeries(Series s, SeriesStore? store)
    {
        if (store is null) return;
        s.AllTimeMax = store.AllTimeMax;
        s.AllTimeMaxAtUtc = store.AllTimeMaxAtUtc;
        foreach (var (hour, max) in store.HourlyMax)
            s.HourlyMax[hour] = max;
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
                Live = ToStore(_live),
                Working = ToStore(_working),
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

    private static SeriesStore ToStore(Series s) => new()
    {
        AllTimeMax = s.AllTimeMax,
        AllTimeMaxAtUtc = s.AllTimeMaxAtUtc,
        HourlyMax = new Dictionary<string, int>(s.HourlyMax, StringComparer.Ordinal),
    };
}

/// <summary>One hour of the concurrency history: the UTC clock-hour key ("yyyy-MM-ddTHH") and the max
/// concurrent count seen in that hour.</summary>
public sealed class ConcurrencyHourDto
{
    public string Hour { get; set; } = "";
    public int Max { get; set; }
}

/// <summary>One tracked concurrency dimension (live or working) for the dashboard / agent API.</summary>
public sealed class ConcurrencySeriesDto
{
    /// <summary>The most recently observed count.</summary>
    public int Current { get; set; }
    /// <summary>The highest count ever observed.</summary>
    public int AllTimeMax { get; set; }
    /// <summary>When the all-time peak was observed (UTC), or null if never.</summary>
    public DateTime? AllTimeMaxAtUtc { get; set; }
    /// <summary>The highest count in the last 7 days, derived from the hourly history.</summary>
    public int WeeklyMax { get; set; }
    /// <summary>The per-hour max history, oldest hour first.</summary>
    public List<ConcurrencyHourDto> Hourly { get; set; } = new();
}

/// <summary>Both concurrency dimensions: sessions loaded/running (live) and sessions actively working.</summary>
public sealed record ConcurrencySnapshot(ConcurrencySeriesDto Live, ConcurrencySeriesDto Working);
