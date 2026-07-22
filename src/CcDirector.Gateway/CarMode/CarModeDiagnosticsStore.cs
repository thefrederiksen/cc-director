using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The durable, Gateway-local store of Car Mode turn timing records (Car Mode performance round). Every
/// turn the browser posts ONE compact <see cref="CarModeDiagnosticsRecord"/> here; the private
/// GET /carmode/diagnostics dashboard reads them back with no cloud round trip. This is the observability
/// foundation the owner asked for - a real per-stage breakdown of where a Car Mode turn spends its time.
///
/// Retention is by AGE (drop records older than <see cref="RetentionDays"/> days, the owner's stated
/// 90-day intent), with a generous PER-PARTITION hard cap (<see cref="MaxRecords"/> records per device) ONLY
/// as an unbounded-growth guard so the file can never grow without bound if the owner walks-and-talks far
/// more than expected in a window. The cap is per device on purpose: a process-wide cap would let one
/// credential's write trip an eviction that deletes ANOTHER credential's rows, which is a cross-partition
/// deletion. A write only ever prunes the caller's OWN partition; startup prunes every partition within
/// itself. Persisted as a single VERSIONED JSON document (a <see cref="StoreDocument"/> envelope) with an
/// atomic temp-write + rename (a concurrent reader or a crash mid-write never sees a half-written store); a
/// corrupt file is quarantined and the store starts empty, exactly like
/// <see cref="Stats.GatewayInputStatsAggregator"/>.
///
/// THE DOCUMENT IS VERSIONED, AND A PRE-VERSION DOCUMENT IS NOT TRUSTED. Before this store was partitioned
/// on the authenticated credential, an earlier endpoint stamped each record's DeviceHash by independently
/// reparsing the raw request credentials and preferring any Bearer value - even when the gate had REJECTED
/// that Bearer and authenticated a cookie instead. A nonblank hash on one of those legacy records is
/// therefore NOT a trustworthy partition key: serving it under the partitioned reads could hand one
/// credential's turns to another. So a legacy (unversioned) or older-version document is quarantined whole
/// on load and the store starts empty - blank-only cleanup is not enough, because the untrustworthy case is
/// precisely a NONBLANK legacy hash. Only records this store wrote at <see cref="StoreVersion"/> or above,
/// whose DeviceHash <see cref="Add"/> stamped from the gate's own accepted credential, are trusted.
///
/// Only timings and small counts are ever kept - never the text of what was said or heard.
///
/// PARTITIONED BY DEVICE CREDENTIAL. Every record belongs to exactly one device partition - the short
/// one-way hash of the caller's own credential that <see cref="CarModeDeviceHash"/> derives from the
/// request, never from the posted body. The device credential is the safe discriminator here for three
/// reasons: it is trusted (the Gateway derives it from the authenticated request), the other Car Mode
/// stores - conversation, pending action, subject - are already keyed the same way, so this is consistent
/// rather than novel, and diagnostics are per-device operational data (a turn's timings describe THAT phone's
/// microphone, transcode, and playback). Making diagnostics follow a person across their devices is a
/// PRODUCT question, not this security partition, and is deliberately not built here.
///
/// The hash is derived from THE CREDENTIAL THE AUTHENTICATION GATE ACCEPTED, which the endpoint reads from
/// the request the gate stamped it on - never from a second reading of the raw headers, which would be a
/// second authentication decision that can disagree with the first.
///
/// Both the write and the reads take the partition: <see cref="Add"/> stamps the trusted hash onto the
/// record it stores (so a record can never be filed under a partition the caller chose), and
/// <see cref="Recent"/> and <see cref="Count"/> return only that partition. A read-only filter would be a
/// deferred leak - unpartitioned records would keep accumulating behind it - so the write records the
/// partition too. The caller-supplied <c>TurnId</c> is never used as a discriminator.
/// </summary>
public sealed class CarModeDiagnosticsStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = false };

    /// <summary>The persisted document format version. Bumped to 2 when the store began partitioning on the
    ///  AUTHENTICATED credential; a document written before that (unversioned, or any version below this) is
    ///  quarantined on load rather than trusted, because its per-record attribution predates the fix. Bump
    ///  this again if a future change ever invalidates the trust in an existing record's DeviceHash.</summary>
    private const int StoreVersion = 2;

    /// <summary>Keep records for this many days (the owner asked specifically for 90 days).</summary>
    private const int RetentionDays = 90;

    /// <summary>An unbounded-growth guard only, applied PER DEVICE PARTITION - far above any realistic 90-day
    ///  volume for a single device. When a device's own partition exceeds this, ITS OWN oldest records are
    ///  evicted (see <see cref="PruneLocked"/>); one device's writes never touch another device's partition.</summary>
    private const int MaxRecords = 10000;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Action<string> _log;
    private readonly int _maxRecords;

    // Newest last (append order). Reads hand back a newest-first copy for the dashboard.
    private readonly List<CarModeDiagnosticsRecord> _records = new();

    /// <param name="path">The durable store file. Defaults to carmode-diagnostics.json under the cc-director
    ///  storage root, beside the other Gateway stores.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public CarModeDiagnosticsStore(string? path = null, Action<string>? log = null)
        : this(path, log, MaxRecords)
    {
    }

    /// <summary>Test seam: the same store with a small PER-PARTITION growth-guard cap, so the per-device
    ///  eviction can be driven with a handful of records per device instead of ten thousand.</summary>
    internal CarModeDiagnosticsStore(string? path, Action<string>? log, int maxRecords)
    {
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "carmode-diagnostics.json")
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
    public int Add(string deviceHash, CarModeDiagnosticsRecord? record)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A diagnostics write needs the caller's device partition.", nameof(deviceHash));
        if (record is null) return Count(deviceHash);
        var owned = record with { DeviceHash = deviceHash };
        lock (_lock)
        {
            _records.Add(owned);
            // A write prunes ONLY the caller's own partition - never another device's - for BOTH the age
            // retention AND the growth cap, so one credential's Add can never mutate or delete another
            // credential's rows (not by cap, and not by age either). Other partitions' expired rows are aged
            // on the next startup Load (which ages every partition within itself), never by a foreign caller.
            PruneLocked(DateTime.UtcNow, capPartition: deviceHash);
            Save();
            var held = MineNewestFirstLocked(deviceHash).Count;
            _log($"[CarModeDiagnostics] recorded turn {owned.TurnId}: total={owned.TotalTurnMs:F0}ms, brain={owned.BrainMs:F0}ms, "
                + $"fleetReads={owned.FleetReadCount}, models={owned.ModelCallCount}; this device now holds {held} of {_records.Count}");
            return held;
        }
    }

    /// <summary>The most recent <paramref name="limit"/> records BELONGING TO <paramref name="deviceHash"/>,
    ///  newest first, for the dashboard. Another device's records are never returned.</summary>
    /// <param name="deviceHash">The trusted, credential-derived partition from
    ///  <see cref="CarModeDeviceHash.Of"/>. Blank is a programming error and throws, so a missing partition
    ///  can never widen the read to every device.</param>
    public IReadOnlyList<CarModeDiagnosticsRecord> Recent(string deviceHash, int limit)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A diagnostics read needs the caller's device partition.", nameof(deviceHash));
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
            throw new ArgumentException("A diagnostics read needs the caller's device partition.", nameof(deviceHash));
        lock (_lock) return MineNewestFirstLocked(deviceHash).Count;
    }

    /// <summary>Deletes only the caller's device partition and returns the number removed.</summary>
    public int Clear(string deviceHash)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            throw new ArgumentException("A diagnostics clear needs the caller's device partition.", nameof(deviceHash));
        lock (_lock)
        {
            var removed = _records.RemoveAll(record =>
                string.Equals(record.DeviceHash, deviceHash, StringComparison.Ordinal));
            if (removed > 0)
                Save();
            return removed;
        }
    }

    /// <summary>THE ONE PLACE a device's records are selected. Every read - the record list and the count -
    ///  goes through this single filter, so the partition cannot be right in one read and wrong in another,
    ///  and there is exactly one line to get right. Newest first. Caller holds the lock.</summary>
    private List<CarModeDiagnosticsRecord> MineNewestFirstLocked(string deviceHash)
    {
        var mine = new List<CarModeDiagnosticsRecord>();
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_records[i].DeviceHash, deviceHash, StringComparison.Ordinal))
                mine.Add(_records[i]);
        }
        return mine;
    }

    // Drop records older than the retention window, then enforce the growth cap. BOTH steps are strictly
    // partition-local when capPartition is set (a write): only THAT device's partition is aged and capped, so
    // a write can never remove another device's rows - not by cap, and not by age. A caller's Add must NEVER
    // mutate, prune, or delete a row outside its own partition. When capPartition is null (load, which is not
    // attributable to any credential), every partition is aged and capped WITHIN ITSELF. Returns HOW MANY
    // records were removed, so a caller that is not already going to persist (Load) knows it must. Caller
    // holds the lock.
    private int PruneLocked(DateTime nowUtc, string? capPartition)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        // Age retention. On a write, age ONLY the writer's own partition - a foreign caller aging (and thus
        // deleting) another device's expired rows is exactly the cross-partition mutation this store forbids.
        // On load, age every partition.
        int removedByAge = capPartition is null
            ? _records.RemoveAll(r => !WithinRetention(r.ReceivedAtUtc, cutoff))
            : _records.RemoveAll(r => string.Equals(r.DeviceHash, capPartition, StringComparison.Ordinal)
                                      && !WithinRetention(r.ReceivedAtUtc, cutoff));
        if (removedByAge > 0)
            _log($"[CarModeDiagnostics] pruned {removedByAge} record(s) older than {RetentionDays} days");

        // Growth guard, strictly partition-local. The cap is PER DEVICE: a partition over its cap loses its
        // OWN oldest records and nothing else. On a write we cap only the writer's partition, so a write can
        // never delete another device's data; on load we cap each partition within itself. There is no
        // cross-partition comparison and therefore no way for one device to push another device's records out.
        var evicted = 0;
        if (capPartition is null)
        {
            foreach (var hash in DistinctPartitionsLocked())
                evicted += EvictOldestOfPartitionLocked(hash);
        }
        else
        {
            evicted += EvictOldestOfPartitionLocked(capPartition);
        }
        if (evicted > 0)
            _log($"[CarModeDiagnostics] growth guard: dropped {evicted} record(s), each from its own device partition, to keep every partition at or under {_maxRecords}");

        return removedByAge + evicted;
    }

    // Evict the OLDEST records of ONE device's partition until it is at or under the per-partition cap. Never
    // touches any other partition. Returns how many it removed. Caller holds the lock.
    private int EvictOldestOfPartitionLocked(string deviceHash)
    {
        var count = 0;
        foreach (var r in _records)
            if (string.Equals(r.DeviceHash, deviceHash, StringComparison.Ordinal)) count++;

        var evicted = 0;
        while (count > _maxRecords)
        {
            // FindIndex returns the LOWEST index, which is the oldest record of this partition (append order).
            var index = _records.FindIndex(r => string.Equals(r.DeviceHash, deviceHash, StringComparison.Ordinal));
            if (index < 0) break; // unreachable: count > cap >= 1 means this partition has a record.
            _records.RemoveAt(index);
            count--;
            evicted++;
        }
        return evicted;
    }

    // Each distinct device hash present right now. Caller holds the lock.
    private List<string> DistinctPartitionsLocked()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var r in _records)
            if (seen.Add(r.DeviceHash)) order.Add(r.DeviceHash);
        return order;
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
            _log($"[CarModeDiagnostics] Load: no store file at {_path}; starting empty");
            return;
        }

        var text = File.ReadAllText(_path);

        // Inspect the root shape first. A pre-version document is a BARE ARRAY of records; the current
        // document is an OBJECT envelope with a Version. A legacy array (or an older-version envelope) is
        // NOT trusted - its per-record DeviceHash predates partitioning on the authenticated credential and
        // may have been stamped from a credential the gate rejected - so it is quarantined whole and the
        // store starts empty. Trusting a nonblank legacy hash is exactly the cross-credential leak we refuse;
        // blank-only cleanup would not catch it.
        JsonValueKind rootKind;
        try
        {
            using var probe = JsonDocument.Parse(text);
            rootKind = probe.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (rootKind == JsonValueKind.Array)
        {
            Quarantine("legacy unversioned store (records predate partitioning on the authenticated credential); attribution not trustworthy");
            return;
        }

        StoreDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<StoreDocument>(text, FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }
        if (doc is null)
        {
            Quarantine("file deserialized to null (no store document)");
            return;
        }
        if (doc.Version < StoreVersion)
        {
            Quarantine($"store version {doc.Version} predates the authenticated-credential partition (need v{StoreVersion}); attribution not trustworthy");
            return;
        }

        _records.AddRange(doc.Records ?? new List<CarModeDiagnosticsRecord>());

        // Every surviving record was written at this store version or above, where Add stamps the DeviceHash
        // from the credential THE GATE ACCEPTED, so its attribution is trusted. A record with a BLANK hash is
        // the one case with no recorded attribution: it belongs to no device, no partitioned read could ever
        // return it, and guessing an owner for it would be exactly the invented attribution we refuse. Those
        // are purged here rather than kept as unreachable residue.
        var unattributed = _records.RemoveAll(r => string.IsNullOrWhiteSpace(r.DeviceHash));
        if (unattributed > 0)
            _log($"[CarModeDiagnostics] Load: purged {unattributed} record(s) with no device partition (unattributable; never disclosed)");

        var pruned = PruneLocked(DateTime.UtcNow, capPartition: null);

        // The purge and the retention prune are GUARANTEES, so they happen on their own terms: whatever load
        // removes is written back to the durable file IMMEDIATELY. Removing it from memory only and waiting
        // for the next write to flush is not cleanup - it is cleanup as a side effect of unrelated activity,
        // which works whenever something happens to write and never happens on a Gateway where no further
        // turn arrives. The unattributed record would then sit in the file indefinitely, waiting for a future
        // unfiltered reader, while the log line claimed it had been purged.
        if (unattributed + pruned > 0)
        {
            Save();
            _log($"[CarModeDiagnostics] Load: rewrote {_path} after removing {unattributed + pruned} record(s)");
        }

        _log($"[CarModeDiagnostics] Load: restored {_records.Count} record(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        _log($"[CarModeDiagnostics] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
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

            var json = JsonSerializer.Serialize(new StoreDocument(StoreVersion, _records), FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"[CarModeDiagnostics] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }

    /// <summary>The persisted envelope: a version stamp plus the records. The version is what lets load
    ///  distinguish records written under the authenticated-credential partition (trusted) from a pre-version
    ///  document (quarantined). A bare record array on disk is a pre-version document by definition.</summary>
    private sealed record StoreDocument(int Version, List<CarModeDiagnosticsRecord> Records);
}
