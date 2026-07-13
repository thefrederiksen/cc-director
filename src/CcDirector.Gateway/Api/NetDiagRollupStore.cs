using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The hourly quality rollup (Network Diagnostics mission, Phase 1 / Architect Decision 2b as resequenced).
/// ONE bucket per UTC hour (no per-route key - so no cardinality growth and no route/quality conflation),
/// but each bucket carries a HOME vs AWAY split keyed on the MEASURED path (isLanPath), never the front-door
/// ClientPath. That gives home-vs-away quality history from day one for free; P1 STORES the split, the P3
/// dashboard shows overall percent-direct + latency trend, and P4 just turns on the home/away presentation
/// over history that is already accumulating.
///
/// This is the 90-day durable memory (the raw results ring stays recent-depth). Same atomic write-through +
/// corrupt-file quarantine contract as <see cref="CronRunHistoryStore"/>; the file stays small (~24 x 90 =
/// a couple thousand buckets), so a rewrite per fold is cheap.
/// </summary>
public sealed class NetDiagRollupStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>One UTC hour's aggregate. Sums (not medians) so folds are associative; averages derived on read.</summary>
    public sealed class HourBucket
    {
        public string Hour { get; set; } = ""; // "yyyy-MM-ddTHH" UTC
        public int Count { get; set; }
        public double SumLatencyMs { get; set; }
        public double? MinLatencyMs { get; set; }
        public int DirectCount { get; set; }
        public int RelayCount { get; set; }

        // HOME (LAN-direct measured path) sub-sums.
        public int LanCount { get; set; }
        public double SumLatencyLan { get; set; }
        public double? MinLatencyLan { get; set; }
        public double SumDownLan { get; set; }
        public double SumUpLan { get; set; }

        // AWAY (non-LAN / relay measured path) sub-sums.
        public int AwayCount { get; set; }
        public double SumLatencyAway { get; set; }
        public double? MinLatencyAway { get; set; }
        public double SumDownAway { get; set; }
        public double SumUpAway { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, HourBucket> _buckets = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public NetDiagRollupStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>
    /// Fold one observation into its UTC-hour bucket. <paramref name="isLanPath"/> is the MEASURED path type
    /// (a LAN-direct address = home); <paramref name="direct"/> is the ping verdict. Down/up are only present
    /// for client speed-test results (the monitor passes null). Prunes buckets past the 90-day retention.
    /// </summary>
    public void Fold(DateTime whenUtc, double? latencyMs, bool? direct, bool isLanPath, double? downMbps, double? upMbps)
    {
        var hour = HourKey(whenUtc);
        lock (_gate)
        {
            if (!_buckets.TryGetValue(hour, out var b)) { b = new HourBucket { Hour = hour }; _buckets[hour] = b; }

            b.Count++;
            if (latencyMs is { } lat) { b.SumLatencyMs += lat; b.MinLatencyMs = Least(b.MinLatencyMs, lat); }
            if (direct == true) b.DirectCount++;
            else if (direct == false) b.RelayCount++;

            if (isLanPath)
            {
                b.LanCount++;
                if (latencyMs is { } l) { b.SumLatencyLan += l; b.MinLatencyLan = Least(b.MinLatencyLan, l); }
                if (downMbps is { } d) b.SumDownLan += d;
                if (upMbps is { } u) b.SumUpLan += u;
            }
            else
            {
                b.AwayCount++;
                if (latencyMs is { } l) { b.SumLatencyAway += l; b.MinLatencyAway = Least(b.MinLatencyAway, l); }
                if (downMbps is { } d) b.SumDownAway += d;
                if (upMbps is { } u) b.SumUpAway += u;
            }

            Prune(whenUtc);
            Save();
        }
    }

    /// <summary>All retained buckets, oldest hour first.</summary>
    public IReadOnlyList<HourBucket> All()
    {
        lock (_gate)
            return _buckets.Values.OrderBy(b => b.Hour, StringComparer.Ordinal).ToList();
    }

    public static string HourKey(DateTime whenUtc)
        => whenUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);

    private static double? Least(double? cur, double v) => cur is null ? v : Math.Min(cur.Value, v);

    private void Prune(DateTime nowUtc)
    {
        var cutoff = HourKey(nowUtc - Retention);
        var stale = _buckets.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList();
        foreach (var k in stale) _buckets.Remove(k);
    }

    // ---- persistence (CronRunHistoryStore precedent) ----

    private sealed class StoreFile
    {
        public Dictionary<string, HourBucket> Buckets { get; set; } = new(StringComparer.Ordinal);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[NetDiagRollupStore] Load: no store file at {_path}; starting empty");
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

        foreach (var (hour, bucket) in parsed.Buckets)
            if (!string.IsNullOrWhiteSpace(hour) && bucket is not null)
                _buckets[hour] = bucket;

        FileLog.Write($"[NetDiagRollupStore] Load: restored {_buckets.Count} hourly bucket(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[NetDiagRollupStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Buckets = _buckets };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NetDiagRollupStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
