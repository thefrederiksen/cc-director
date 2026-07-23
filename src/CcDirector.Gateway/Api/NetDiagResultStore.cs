using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable store of recent network speed-test results (Network Diagnostics mission, Phase 1). Replaces
/// the in-memory NetDiagResultLog ring so results submitted from the app/Cockpit AND the server-side
/// monitor survive a Gateway restart and can feed the per-device baseline. Newest-first, bounded, and
/// persisted to <c>diagnostics-results.json</c> with the same atomic write-through + corrupt-file
/// quarantine contract as <see cref="CronRunHistoryStore"/>, so a crash mid-write never half-truncates
/// the file and an unreadable file is preserved rather than silently overwritten.
///
/// PARTITIONED BY TENANT (Hosted Multi-Tenancy; unsafe-collection census row 21). Every result carries the
/// client's address, its measured path, and the surface and route it came from - one tenant's operational
/// picture of its own fleet - and before this partition they were all mixed into ONE list that any
/// authenticated caller read in full. There is NO caller-supplied identifier here to namespace, so this is a
/// partition rather than a prefix: each tenant gets its own list, and both the write and the read take the
/// tenant as a REQUIRED parameter.
///
/// THE TENANT IS RECORDED PER RESULT BY THE CONTAINING PARTITION KEY, and deliberately NOT by a field on
/// <see cref="NetDiagResultDto"/>. The DTO is deserialized straight from the request body, so any tenant
/// field on it would be a value the CALLER could claim, which the store would then have to overwrite on
/// every path - a claim surface bought for nothing, because the partition key already records the
/// attribution of every stored record unambiguously and the caller cannot reach it.
///
/// THE CAP IS PER TENANT, NOT GLOBAL. A single shared <see cref="MaxRecords"/> across partitions would let a
/// noisy tenant evict another tenant's results just by writing - suppression and contention, not merely
/// disclosure. Each partition is pruned on its own.
///
/// A PRE-PARTITION FILE IS DELETED FROM THE LIVE STORE, NOT MIGRATED AND NOT QUARANTINED - and that claim
/// stops at the live path. See <see cref="DeletePrePartitionFile"/>.
/// </summary>
public sealed class NetDiagResultStore
{
    /// <summary>Max results retained PER TENANT; older results are pruned (keeps the file bounded). Architect Decision 2a: 50 -> 200.</summary>
    public const int MaxRecords = 200;

    private readonly object _gate = new();
    private readonly string _path;

    /// <summary>Canonical tenant key -> that tenant's results, newest first. The ONLY way in is
    /// <see cref="PartitionFor"/> with a validated tenant, so there is no all-tenants view to read by
    /// accident and no tenant-less way to write.</summary>
    private readonly Dictionary<string, List<NetDiagResultDto>> _tenants = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">The JSON file the store persists to. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public NetDiagResultStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>
    /// Record one result for <paramref name="tenant"/> (newest first), pruning that tenant's partition to
    /// <see cref="MaxRecords"/>, and persist. The tenant must be resolved from the caller's authenticated
    /// credential; an unresolved tenant is a DENY at the route, never a default here.
    /// </summary>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public void Add(TenantId tenant, NetDiagResultDto result)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        lock (_gate)
        {
            var items = PartitionFor(tenant);
            items.Insert(0, result);
            if (items.Count > MaxRecords)
                items.RemoveRange(MaxRecords, items.Count - MaxRecords);
            Save();
            FileLog.Write($"[NetDiagResultStore] Add: tenant={tenant.ToLogString()}, surface={result.Surface}, route={result.Route}, clientPath={result.ClientPath}, latencyMedian={result.LatencyMedianMs}ms, count={items.Count}");
        }
    }

    /// <summary>The most recent results BELONGING TO <paramref name="tenant"/>, newest first (at most
    /// <paramref name="count"/>). There is no unfiltered read: the filter IS the partition lookup, so it
    /// cannot be forgotten separately from the partition.</summary>
    public IReadOnlyList<NetDiagResultDto> Recent(TenantId tenant, int count = MaxRecords)
    {
        lock (_gate)
            return PartitionFor(tenant).Take(Math.Max(0, count)).ToList();
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
            throw new ArgumentException("A diagnostic result needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        if (tenant.IsLocal) return TenantId.Local.Value;
        if (tenant.IsSystem) return TenantId.System.Value;
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot own diagnostic results.", nameof(tenant));
        return tenant.Value;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private List<NetDiagResultDto> PartitionFor(TenantId tenant)
    {
        var key = CanonicalTenantKey(tenant);
        if (!_tenants.TryGetValue(key, out var items))
        {
            items = new List<NetDiagResultDto>();
            _tenants[key] = items;
        }
        return items;
    }

    // ---- persistence (CronRunHistoryStore precedent: atomic write-through + corrupt-file quarantine) ----

    private sealed class StoreFile
    {
        /// <summary>Canonical tenant key -> that tenant's results, newest first. NULL after deserializing a
        /// PRE-PARTITION file, which is exactly how <see cref="Load"/> tells the two shapes apart: the old
        /// document has no <c>tenants</c> property at all, and read into the new shape it would otherwise
        /// arrive as a silent empty rather than as the delete-and-start-empty case it is.</summary>
        public Dictionary<string, List<NetDiagResultDto>>? Tenants { get; set; }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[NetDiagResultStore] Load: no store file at {_path}; starting empty");
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
            DeletePrePartitionFile();
            return;
        }

        foreach (var (tenantKey, results) in parsed.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenantKey) || results is null)
                continue;

            var kept = results.Where(r => r is not null).ToList();
            if (kept.Count > MaxRecords)
                kept.RemoveRange(MaxRecords, kept.Count - MaxRecords);
            _tenants[tenantKey] = kept;
        }

        FileLog.Write($"[NetDiagResultStore] Load: restored {_tenants.Values.Sum(v => v.Count)} result(s) across {_tenants.Count} tenant partition(s) from {_path}");
    }

    /// <summary>
    /// DELETE THE LIVE pre-partition (global, tenant-less) store file and start empty.
    ///
    /// WHAT THIS DOES AND DOES NOT CLAIM. It removes the file at <see cref="_path"/> - the live store this
    /// process reads and writes. That is the whole of what a <c>File.Delete</c> can establish. It says
    /// NOTHING about copies that exist outside this path: a hosted deployment keeps this file on an Azure
    /// Files share (the App Service <c>/home</c> mount), and a share snapshot, a soft-deleted file, a backup,
    /// or filesystem history could hold the same cross-tenant mixture after this call returns. Erasing those
    /// is operational work on the storage account, not something code at this layer can perform or verify,
    /// and it is tracked separately. Deletion from the live representation is not deletion.
    ///
    /// WHY DELETE AND NOT MIGRATE. The old file is one flat list into which EVERY tenant's results were
    /// mixed, with NO per-tenant attribution recorded anywhere in it. There is therefore no tenant to migrate
    /// those records TO - assigning them one would INVENT an attribution that was never recorded, which is
    /// the half-partition this mission forbids, and it would silently hand one tenant another tenant's data.
    ///
    /// WHY DELETE AND NOT QUARANTINE. Quarantine is for a file that could not be READ - it preserves the
    /// evidence of a bug. This file reads perfectly; its problem is that its contents are a cross-tenant
    /// mixture. Renaming it aside would keep it in the live store indefinitely for no benefit, because
    /// nothing will ever be able to attribute it.
    ///
    /// WHY THE COST IS NOTHING REAL. Diagnostic results are ephemeral operational data with no
    /// durability contract - a bounded recent-history ring that prunes itself continuously and that a
    /// Gateway restart was already free to lose. Delete, and partition forward.
    /// </summary>
    private void DeletePrePartitionFile()
    {
        File.Delete(_path);
        FileLog.Write($"[NetDiagResultStore] Load: DELETED the live pre-partition store file at {_path} - it holds a cross-tenant mixture with no per-tenant attribution recorded, so it cannot be migrated without inventing one; starting empty. This removes the live file only; any snapshot, soft-deleted copy or backup of the same path is outside this process and is purged as separate operational work.");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[NetDiagResultStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
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
            FileLog.Write($"[NetDiagResultStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
