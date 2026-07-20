using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The hourly quality rollup (Network Diagnostics mission, Phase 1 / Architect Decision 2b as resequenced).
/// ONE bucket per UTC hour PER TENANT (no per-route key - so no cardinality growth and no route/quality
/// conflation), but each bucket carries a HOME vs AWAY split keyed on the MEASURED path (isLanPath), never
/// the front-door ClientPath. That gives home-vs-away quality history from day one for free; P1 STORES the
/// split, the P3 dashboard shows overall percent-direct + latency trend, and P4 just turns on the home/away
/// presentation over history that is already accumulating.
///
/// This is the 90-day durable memory (the raw results ring stays recent-depth). Same atomic write-through +
/// corrupt-file quarantine contract as <see cref="CronRunHistoryStore"/>; the file stays small (~24 x 90 =
/// a couple thousand buckets per tenant), so a rewrite per fold is cheap.
///
/// KEYED BY TENANT PLUS HOUR (Hosted Multi-Tenancy; unsafe-collection census row 22). The hour comes from
/// SERVER TIME, so every tenant writing at the same moment folded into the SAME bucket: a shared aggregate
/// that any authenticated caller could both read and POISON, because a fold is an addition nobody can
/// attribute afterwards. There is no caller-supplied identifier to namespace, so the fix is a partition:
/// <c>tenant -> hour -> bucket</c>, with the tenant a REQUIRED parameter on the fold AND on the read.
/// Retention pruning is per tenant for the same reason - one tenant's clock-driven prune must not reach
/// into another's history.
///
/// PRE-PARTITION FILES ARE PURGED, NOT MIGRATED AND NOT QUARANTINED. See <see cref="PurgePrePartitionFile"/>.
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

    /// <summary>Canonical tenant key -> that tenant's hour buckets. The ONLY way in is
    /// <see cref="PartitionFor"/> with a validated tenant, so there is no all-tenants aggregate to read or
    /// fold into by accident.</summary>
    private readonly Dictionary<string, Dictionary<string, HourBucket>> _tenants = new(StringComparer.Ordinal);

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
    /// Fold one observation into <paramref name="tenant"/>'s UTC-hour bucket. <paramref name="isLanPath"/> is
    /// the MEASURED path type (a LAN-direct address = home); <paramref name="direct"/> is the ping verdict.
    /// Down/up are only present for client speed-test results (the monitor passes null). Prunes THAT
    /// TENANT'S buckets past the 90-day retention.
    /// </summary>
    public void Fold(TenantId tenant, DateTime whenUtc, double? latencyMs, bool? direct, bool isLanPath, double? downMbps, double? upMbps)
    {
        var hour = HourKey(whenUtc);
        lock (_gate)
        {
            var buckets = PartitionFor(tenant);
            if (!buckets.TryGetValue(hour, out var b)) { b = new HourBucket { Hour = hour }; buckets[hour] = b; }

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

            Prune(buckets, whenUtc);
            Save();
        }
    }

    /// <summary>All retained buckets BELONGING TO <paramref name="tenant"/>, oldest hour first. There is no
    /// unfiltered read: the filter IS the partition lookup, so it cannot be forgotten separately from the
    /// partition.</summary>
    public IReadOnlyList<HourBucket> All(TenantId tenant)
    {
        lock (_gate)
            return PartitionFor(tenant).Values.OrderBy(b => b.Hour, StringComparer.Ordinal).ToList();
    }

    public static string HourKey(DateTime whenUtc)
        => whenUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);

    private static double? Least(double? cur, double v) => cur is null ? v : Math.Min(cur.Value, v);

    private static void Prune(Dictionary<string, HourBucket> buckets, DateTime nowUtc)
    {
        var cutoff = HourKey(nowUtc - Retention);
        var stale = buckets.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList();
        foreach (var k in stale) buckets.Remove(k);
    }

    // ---- tenant canonicalization ----

    /// <summary>
    /// True only for the EXACT form the tenant registry mints: a canonical lowercase GUID. The same guard
    /// <see cref="Voice.GatewayTurnJobStore"/> uses, so two spellings of one account can never become two
    /// partitions, nor one partition be reached by two spellings.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static string CanonicalTenantKey(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A quality rollup needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        if (tenant.IsLocal) return TenantId.Local.Value;
        if (tenant.IsSystem) return TenantId.System.Value;
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot own quality rollups.", nameof(tenant));
        return tenant.Value;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private Dictionary<string, HourBucket> PartitionFor(TenantId tenant)
    {
        var key = CanonicalTenantKey(tenant);
        if (!_tenants.TryGetValue(key, out var buckets))
        {
            buckets = new Dictionary<string, HourBucket>(StringComparer.Ordinal);
            _tenants[key] = buckets;
        }
        return buckets;
    }

    // ---- persistence (CronRunHistoryStore precedent) ----

    private sealed class StoreFile
    {
        /// <summary>Canonical tenant key -> hour key -> bucket. NULL after deserializing a PRE-PARTITION
        /// file, which is exactly how <see cref="Load"/> tells the two shapes apart: the old document has no
        /// <c>tenants</c> property at all, and read into the new shape it would otherwise arrive as a silent
        /// empty rather than as the purge case it is.</summary>
        public Dictionary<string, Dictionary<string, HourBucket>>? Tenants { get; set; }
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

        if (parsed.Tenants is null)
        {
            PurgePrePartitionFile();
            return;
        }

        foreach (var (tenantKey, buckets) in parsed.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenantKey) || buckets is null)
                continue;

            var kept = new Dictionary<string, HourBucket>(StringComparer.Ordinal);
            foreach (var (hour, bucket) in buckets)
                if (!string.IsNullOrWhiteSpace(hour) && bucket is not null)
                    kept[hour] = bucket;
            _tenants[tenantKey] = kept;
        }

        FileLog.Write($"[NetDiagRollupStore] Load: restored {_tenants.Values.Sum(v => v.Count)} hourly bucket(s) across {_tenants.Count} tenant partition(s) from {_path}");
    }

    /// <summary>
    /// DELETE a pre-partition (global, tenant-less) rollup file and start empty.
    ///
    /// WHY DELETE AND NOT MIGRATE. Each old bucket is a SUM over every tenant that folded into that hour,
    /// with no per-tenant attribution recorded anywhere - the addends cannot be separated after the fact.
    /// Assigning a bucket to a tenant would INVENT an attribution that was never recorded, which is the
    /// half-partition this mission forbids; worse than the raw results case, it would hand that tenant a
    /// number that is provably not its own.
    ///
    /// WHY DELETE AND NOT QUARANTINE. Quarantine is for a file that could not be READ - it preserves the
    /// evidence of a bug. This file reads perfectly; its problem is that its contents are a cross-tenant
    /// mixture. Renaming it aside would leave that live liability on disk indefinitely for no benefit,
    /// because nothing will ever be able to attribute it.
    ///
    /// WHY THE COST IS NOTHING REAL. This is ephemeral operational telemetry with no durability contract -
    /// a self-pruning quality trend, not a record anything is owed. Purge, and partition forward.
    /// </summary>
    private void PurgePrePartitionFile()
    {
        File.Delete(_path);
        FileLog.Write($"[NetDiagRollupStore] Load: PURGED pre-partition rollup file at {_path} - its buckets are cross-tenant sums with no per-tenant attribution recorded, so they cannot be migrated without inventing one; starting empty.");
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

            var file = new StoreFile { Tenants = _tenants };
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
