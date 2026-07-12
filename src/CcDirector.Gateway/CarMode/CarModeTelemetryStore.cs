using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The durable, Gateway-local store of Car Mode turn timing records (Car Mode performance round). Every
/// turn the browser posts ONE compact <see cref="CarModeTelemetryRecord"/> here; the private
/// GET /carmode/telemetry dashboard reads them back with no cloud round trip. This is the observability
/// foundation the owner asked for - a real per-stage breakdown of where a Car Mode turn spends its time.
///
/// Retention is by AGE (drop records older than <see cref="RetentionDays"/> days, the owner's stated
/// 90-day intent), with a generous hard cap (<see cref="MaxRecords"/>) ONLY as an unbounded-growth guard so
/// the file can never grow without bound if the owner walks-and-talks far more than expected in a window.
/// Persisted as a single JSON document with an atomic temp-write + rename (a concurrent reader or a crash
/// mid-write never sees a half-written store); a corrupt file is quarantined and the store starts empty,
/// exactly like <see cref="Stats.GatewayInputStatsAggregator"/>.
///
/// Only timings and small counts are ever kept - never the text of what was said or heard.
/// </summary>
public sealed class CarModeTelemetryStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = false };

    /// <summary>Keep records for this many days (the owner asked specifically for 90 days).</summary>
    private const int RetentionDays = 90;

    /// <summary>An unbounded-growth guard only - far above any realistic 90-day volume. When the store
    ///  somehow exceeds this, the oldest records are dropped first so the newest are always kept.</summary>
    private const int MaxRecords = 10000;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Action<string> _log;

    // Newest last (append order). Reads hand back a newest-first copy for the dashboard.
    private readonly List<CarModeTelemetryRecord> _records = new();

    /// <param name="path">The durable store file. Defaults to carmode-telemetry.json under the cc-director
    ///  storage root, beside the other Gateway stores.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public CarModeTelemetryStore(string? path = null, Action<string>? log = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "carmode-telemetry.json")
            : path!;
        _log = log ?? FileLog.Write;
        Load();
    }

    /// <summary>Record one turn's timing, then prune by age (and the growth-guard cap) and persist. A null
    ///  record is ignored. The stored count after the add is returned so the endpoint can log it.</summary>
    public int Add(CarModeTelemetryRecord? record)
    {
        if (record is null) return Count();
        lock (_lock)
        {
            _records.Add(record);
            PruneLocked(DateTime.UtcNow);
            Save();
            _log($"[CarModeTelemetry] recorded turn {record.TurnId}: total={record.TotalTurnMs:F0}ms, brain={record.BrainMs:F0}ms, "
                + $"fleetReads={record.FleetReadCount}, models={record.ModelCallCount}; store now holds {_records.Count}");
            return _records.Count;
        }
    }

    /// <summary>The most recent <paramref name="limit"/> records, newest first, for the dashboard.</summary>
    public IReadOnlyList<CarModeTelemetryRecord> Recent(int limit)
    {
        if (limit <= 0) limit = 100;
        lock (_lock)
        {
            var start = Math.Max(0, _records.Count - limit);
            var slice = _records.GetRange(start, _records.Count - start);
            slice.Reverse();
            return slice;
        }
    }

    /// <summary>How many records are held right now.</summary>
    public int Count()
    {
        lock (_lock) return _records.Count;
    }

    // Drop records older than the retention window, then, only if still over the growth-guard cap, drop the
    // oldest until under it. Caller holds the lock.
    private void PruneLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var removedByAge = _records.RemoveAll(r => !WithinRetention(r.ReceivedAtUtc, cutoff));
        if (removedByAge > 0)
            _log($"[CarModeTelemetry] pruned {removedByAge} record(s) older than {RetentionDays} days");

        if (_records.Count > MaxRecords)
        {
            var overflow = _records.Count - MaxRecords;
            _records.RemoveRange(0, overflow);
            _log($"[CarModeTelemetry] growth guard: dropped {overflow} oldest record(s) to stay at {MaxRecords}");
        }
    }

    /// <summary>True when a record's received-at stamp is at or after the cutoff. A record with an
    ///  unparseable stamp is KEPT (treated as fresh) rather than silently discarded.</summary>
    private static bool WithinRetention(string receivedAtUtc, DateTime cutoff)
    {
        if (!DateTime.TryParse(receivedAtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
            return true;
        return when >= cutoff;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _log($"[CarModeTelemetry] Load: no store file at {_path}; starting empty");
            return;
        }

        List<CarModeTelemetryRecord>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<CarModeTelemetryRecord>>(File.ReadAllText(_path), FileJsonOptions);
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

        _records.AddRange(parsed);
        PruneLocked(DateTime.UtcNow);
        _log($"[CarModeTelemetry] Load: restored {_records.Count} record(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        _log($"[CarModeTelemetry] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    // Write-through under the lock: serialize the whole store and atomically replace the file (temp +
    // rename) so a concurrent reader or a crash mid-write never sees a half-written store. A failed save is
    // a LOGGED error that propagates - never a silent skip (no-fallback).
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_records, FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"[CarModeTelemetry] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
