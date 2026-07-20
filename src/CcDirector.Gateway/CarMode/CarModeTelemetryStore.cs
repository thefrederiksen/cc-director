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
///
/// PARTITIONED BY DEVICE CREDENTIAL. Every record belongs to exactly one device partition - the short
/// one-way hash of the caller's own credential that <see cref="CarModeDeviceHash"/> derives from the
/// request, never from the posted body. The device credential is the safe discriminator here for three
/// reasons: it is trusted (the Gateway derives it from the authenticated request), the other Car Mode
/// stores - conversation, pending action, subject - are already keyed the same way, so this is consistent
/// rather than novel, and telemetry is per-device operational data (a turn's timings describe THAT phone's
/// microphone, transcode, and playback). Making telemetry follow a person across their devices is a
/// PRODUCT question, not this security partition, and is deliberately not built here.
///
/// Both the write and the reads take the partition: <see cref="Add"/> stamps the trusted hash onto the
/// record it stores (so a record can never be filed under a partition the caller chose), and
/// <see cref="Recent"/> and <see cref="Count"/> return only that partition. A read-only filter would be a
/// deferred leak - unpartitioned records would keep accumulating behind it - so the write records the
/// partition too. The caller-supplied <c>TurnId</c> is never used as a discriminator.
/// </summary>
public sealed class CarModeTelemetryStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = false };

    /// <summary>Keep records for this many days (the owner asked specifically for 90 days).</summary>
    private const int RetentionDays = 90;

    /// <summary>An unbounded-growth guard only - far above any realistic 90-day volume. When the store
    ///  somehow exceeds this, records are evicted from the LARGEST device partition first (see
    ///  <see cref="PruneLocked"/>), so one busy device can never push another device's records out.</summary>
    private const int MaxRecords = 10000;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Action<string> _log;
    private readonly int _maxRecords;

    // Newest last (append order). Reads hand back a newest-first copy for the dashboard.
    private readonly List<CarModeTelemetryRecord> _records = new();

    /// <param name="path">The durable store file. Defaults to carmode-telemetry.json under the cc-director
    ///  storage root, beside the other Gateway stores.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public CarModeTelemetryStore(string? path = null, Action<string>? log = null)
        : this(path, log, MaxRecords)
    {
    }

    /// <summary>Test seam: the same store with a small growth-guard cap, so the partition-fair eviction can
    ///  be driven with a handful of records instead of ten thousand.</summary>
    internal CarModeTelemetryStore(string? path, Action<string>? log, int maxRecords)
    {
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "carmode-telemetry.json")
            : path!;
        _log = log ?? FileLog.Write;
        _maxRecords = maxRecords;
        Load();
    }

    /// <summary>Record one turn's timing INTO <paramref name="deviceHash"/>'s partition, then prune by age
    ///  (and the growth-guard cap) and persist. The stored record's device hash is taken from
    ///  <paramref name="deviceHash"/> and nothing else, so the write always records the partition and a
    ///  caller can never file a record under another device. A null record is ignored. The count held for
    ///  THAT device after the add is returned so the endpoint can report it.</summary>
    /// <param name="deviceHash">The trusted, credential-derived partition from
    ///  <see cref="CarModeDeviceHash.Of"/>. Blank is a programming error and throws - it is never quietly
    ///  turned into a shared bucket, because that would silently un-partition the write.</param>
    public int Add(string deviceHash, CarModeTelemetryRecord? record)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A telemetry write needs the caller's device partition.", nameof(deviceHash));
        if (record is null) return Count(deviceHash);
        var owned = record with { DeviceHash = deviceHash };
        lock (_lock)
        {
            _records.Add(owned);
            PruneLocked(DateTime.UtcNow);
            Save();
            var held = MineNewestFirstLocked(deviceHash).Count;
            _log($"[CarModeTelemetry] recorded turn {owned.TurnId}: total={owned.TotalTurnMs:F0}ms, brain={owned.BrainMs:F0}ms, "
                + $"fleetReads={owned.FleetReadCount}, models={owned.ModelCallCount}; this device now holds {held} of {_records.Count}");
            return held;
        }
    }

    /// <summary>The most recent <paramref name="limit"/> records BELONGING TO <paramref name="deviceHash"/>,
    ///  newest first, for the dashboard. Another device's records are never returned.</summary>
    /// <param name="deviceHash">The trusted, credential-derived partition from
    ///  <see cref="CarModeDeviceHash.Of"/>. Blank is a programming error and throws, so a missing partition
    ///  can never widen the read to every device.</param>
    public IReadOnlyList<CarModeTelemetryRecord> Recent(string deviceHash, int limit)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A telemetry read needs the caller's device partition.", nameof(deviceHash));
        if (limit <= 0) limit = 100;
        lock (_lock)
        {
            var mine = MineNewestFirstLocked(deviceHash);
            return mine.Count <= limit ? mine : mine.GetRange(0, limit);
        }
    }

    /// <summary>How many records <paramref name="deviceHash"/> holds right now. Deliberately per-device:
    ///  a process-wide total is another device's data, disclosed as an aggregate.</summary>
    public int Count(string deviceHash)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A telemetry read needs the caller's device partition.", nameof(deviceHash));
        lock (_lock) return MineNewestFirstLocked(deviceHash).Count;
    }

    /// <summary>THE ONE PLACE a device's records are selected. Every read - the record list and the count -
    ///  goes through this single filter, so the partition cannot be right in one read and wrong in another,
    ///  and there is exactly one line to get right. Newest first. Caller holds the lock.</summary>
    private List<CarModeTelemetryRecord> MineNewestFirstLocked(string deviceHash)
    {
        var mine = new List<CarModeTelemetryRecord>();
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_records[i].DeviceHash, deviceHash, StringComparison.Ordinal))
                mine.Add(_records[i]);
        }
        return mine;
    }

    // Drop records older than the retention window, then, only if still over the growth-guard cap, drop the
    // oldest until under it. Caller holds the lock.
    private void PruneLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var removedByAge = _records.RemoveAll(r => !WithinRetention(r.ReceivedAtUtc, cutoff));
        if (removedByAge > 0)
            _log($"[CarModeTelemetry] pruned {removedByAge} record(s) older than {RetentionDays} days");

        // Growth guard, partition-fair. The cap is on the FILE, but the eviction is never allowed to let one
        // device push another device's records out - that would be suppression across the partition, not just
        // a size guard. So each eviction takes the OLDEST record of whichever device currently holds the MOST
        // records: a device can only lose a record while it is the largest partition, which is exactly the
        // device that is over its share. Ties break on the device hash so the choice is deterministic.
        var evicted = 0;
        while (_records.Count > _maxRecords)
        {
            var largest = LargestPartitionLocked();
            var index = _records.FindIndex(r => string.Equals(r.DeviceHash, largest, StringComparison.Ordinal));
            if (index < 0) break; // unreachable: the largest partition has at least one record.
            _records.RemoveAt(index);
            evicted++;
        }
        if (evicted > 0)
            _log($"[CarModeTelemetry] growth guard: dropped {evicted} record(s) from the largest device partition(s) to stay at {_maxRecords}");
    }

    // The device hash holding the most records right now. Caller holds the lock.
    private string LargestPartitionLocked()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _records)
            counts[r.DeviceHash] = counts.TryGetValue(r.DeviceHash, out var n) ? n + 1 : 1;

        var bestKey = "";
        var bestCount = -1;
        foreach (var pair in counts)
        {
            if (pair.Value > bestCount || (pair.Value == bestCount && string.CompareOrdinal(pair.Key, bestKey) < 0))
            {
                bestKey = pair.Key;
                bestCount = pair.Value;
            }
        }
        return bestKey;
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

        // Records already on disk carry the device hash the SERVER stamped on them when they were written -
        // it has been a server-filled field since the store shipped - so partitioning the reads attributes
        // nothing that was not already recorded. There is no migration to run and nothing to invent.
        // A record with a BLANK hash is the one case with no recorded attribution: it belongs to no device,
        // no partitioned read could ever return it, and guessing an owner for it would be exactly the
        // invented attribution we refuse. Those are purged here rather than kept as unreachable residue.
        var unattributed = _records.RemoveAll(r => string.IsNullOrWhiteSpace(r.DeviceHash));
        if (unattributed > 0)
            _log($"[CarModeTelemetry] Load: purged {unattributed} record(s) with no device partition (unattributable; never disclosed)");

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
