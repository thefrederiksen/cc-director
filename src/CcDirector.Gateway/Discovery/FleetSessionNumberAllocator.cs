using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// The per-tenant authority for the short three-digit session numbers (100-999) shown on the rail
/// (issue #1292). One instance lives on the Gateway, but its state is PARTITIONED BY TENANT: a number
/// names exactly ONE session across every Director a single tenant owns - unlike the old per-Director
/// allocator, where each Director counted independently from 100 and the same number appeared on several
/// Directors.
///
/// TENANT PARTITIONING (audit H2). Every piece of state - the session-to-number map, the in-use set, and
/// the 100-999 pool it draws from - lives inside a per-tenant partition, keyed by <see cref="TenantId"/>.
/// Every operation takes the caller's tenant (server-resolved at the endpoint from the authenticated
/// device key, never from client input) and only ever reads or writes that tenant's own partition. This
/// closes a whole family of cross-tenant faults the old bare-id store had, because a director id and a
/// session id are unique only WITHIN a tenant, not across the fleet:
///   - Allocate for tenant B's session could read tenant A's assignment for the same id and hand back A's
///     number; Release for one tenant could free the other's; a same-id Director removal in one tenant
///     freed the other's assignments (surfacing as a duplicated rail number for the innocent tenant).
///   - The pool was a single global 900-number space, so one tenant spinning up sessions exhausted
///     numbers for every other tenant. Each tenant now draws from its OWN 100-999 pool, so exhaustion -
///     if it ever happens - is confined to the tenant that caused it.
/// On self-host every caller resolves to <see cref="TenantId.Local"/>, so there is one partition and the
/// behavior is exactly as before.
///
/// Every Director asks the Gateway for a number when it creates a session (<see cref="Allocate"/>). The
/// Gateway hands out the lowest free number IN THAT TENANT'S PARTITION, records which session (and
/// Director) owns it, and guarantees it is unique across that tenant's fleet.
///
/// Two bands (the issue's refinement), applied per tenant:
///   - The COORDINATED band, <see cref="MinNumber"/>..<see cref="CoordinatedMaxNumber"/> (100-799),
///     is what the Gateway hands out. It fills from the low end.
///   - The OFFLINE band, <see cref="CoordinatedMaxNumber"/>+1..<see cref="MaxNumber"/> (800-999), is
///     left clear for Directors that cannot reach the Gateway at creation time. Those Directors pick a
///     random number in that band locally. Keeping the coordinated hand-outs in the low band means a
///     random offline pick is very unlikely to collide in normal use. The Gateway only spills into the
///     offline band when a tenant's coordinated band is fully exhausted (700 concurrent sessions).
///
/// The Gateway also learns numbers it did NOT hand out - a number a Director assigned offline, or any
/// number still in use after a Gateway restart - via <see cref="Adopt"/>, called as the fleet is
/// aggregated. Adopt only ever marks a number in use; it never frees one, so a momentarily-unreachable
/// Director (whose sessions drop out of the aggregation) can never have its numbers reclaimed and
/// re-handed. Numbers are freed only by an explicit <see cref="Release"/> when a session ends, or by
/// <see cref="ReleaseForDirector"/> when a whole Director is swept from the registry.
///
/// Thread-safe: every public method takes the single lock. The lock guards the whole tenant map, so it is
/// held for the brief span of one partition's probe/mutate - low contention, and simpler than a lock per
/// partition.
/// </summary>
public sealed class FleetSessionNumberAllocator
{
    /// <summary>Lowest assignable number (inclusive).</summary>
    public const int MinNumber = 100;

    /// <summary>Highest number the Gateway hands out from before it spills into the offline band.</summary>
    public const int CoordinatedMaxNumber = 799;

    /// <summary>Highest assignable number (inclusive). 800-999 is the offline band.</summary>
    public const int MaxNumber = 999;

    private readonly object _lock = new();

    /// <summary>One partition per tenant; each holds that tenant's own session map and in-use set (its own pool).</summary>
    private readonly Dictionary<TenantId, TenantPool> _byTenant = new();

    private readonly record struct Assignment(int Number, string DirectorId);

    /// <summary>One tenant's private allocation state: its session-to-number map and its own 100-999 in-use set.</summary>
    private sealed class TenantPool
    {
        public readonly Dictionary<string, Assignment> BySession = new(StringComparer.Ordinal);
        public readonly HashSet<int> InUse = new();
    }

    /// <summary>The tenant's partition, creating it on first use. Caller holds the lock.</summary>
    private TenantPool PoolFor(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var pool))
        {
            pool = new TenantPool();
            _byTenant[tenant] = pool;
        }
        return pool;
    }

    /// <summary>Count of numbers currently reserved in <paramref name="tenant"/>'s partition. For tests and diagnostics.</summary>
    public int InUseCount(TenantId tenant)
    {
        lock (_lock)
            return _byTenant.TryGetValue(tenant, out var pool) ? pool.InUse.Count : 0;
    }

    /// <summary>The number currently assigned to <paramref name="sessionId"/> in <paramref name="tenant"/>'s partition, or null if none.</summary>
    public int? NumberFor(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_lock)
        {
            if (!_byTenant.TryGetValue(tenant, out var pool)) return null;
            return pool.BySession.TryGetValue(sessionId, out var a) ? a.Number : (int?)null;
        }
    }

    /// <summary>
    /// Hand out a number for <paramref name="sessionId"/> IN <paramref name="tenant"/>'s partition, unique
    /// across that tenant's fleet, owned by <paramref name="directorId"/>. Idempotent: asking again for a
    /// session that already has a number returns the SAME number, so a retry never double-assigns. Fills the
    /// coordinated band (100-799) from the low end, spilling into the offline band (800-999) only when the
    /// coordinated band is full. Returns null only when every number 100-999 IN THIS TENANT is in use (that
    /// tenant's pool exhausted) - the Director then shows the session without a number rather than blocking
    /// real work over a cosmetic handle.
    /// </summary>
    public int? Allocate(TenantId tenant, string sessionId, string directorId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        lock (_lock)
        {
            var pool = PoolFor(tenant);
            if (pool.BySession.TryGetValue(sessionId, out var existing))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {sessionId} already has {existing.Number} (idempotent, tenant={tenant.ToLogString()})");
                return existing.Number;
            }

            var chosen = FirstFree(pool, MinNumber, CoordinatedMaxNumber) ?? FirstFree(pool, CoordinatedMaxNumber + 1, MaxNumber);
            if (chosen is not int number)
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: pool exhausted ({pool.InUse.Count} in use), no number for {sessionId} (tenant={tenant.ToLogString()})");
                return null;
            }

            pool.InUse.Add(number);
            pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {number} -> {sessionId} (director={directorId}, {pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
            return number;
        }
    }

    /// <summary>
    /// Mark a number the Gateway did NOT hand out as in use IN <paramref name="tenant"/>'s partition - a
    /// number a Director assigned offline, or a number still live after a Gateway restart - learned as the
    /// fleet is aggregated. Only ever marks a number in use; never frees one, so it is safe to call from the
    /// (possibly partial) fleet view. A no-op when the session is already known, or when the number is out of
    /// range. If the number is already held by a DIFFERENT session in this tenant (a pre-existing offline
    /// collision the Gateway cannot resolve), the first owner keeps it and the conflict is logged.
    /// </summary>
    public void Adopt(TenantId tenant, string sessionId, string directorId, int number)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (number < MinNumber || number > MaxNumber) return;

        lock (_lock)
        {
            var pool = PoolFor(tenant);
            if (pool.BySession.TryGetValue(sessionId, out var existing))
            {
                // Already tracked. Keep the number it already has (its own hand-out or a prior adopt).
                if (existing.Number != number)
                    FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {sessionId} already holds {existing.Number}, ignoring observed {number} (tenant={tenant.ToLogString()})");
                return;
            }

            if (pool.InUse.Contains(number))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} already held by another session; {sessionId} keeps its offline number (pre-existing collision, tenant={tenant.ToLogString()})");
                // Still record ownership so a later Allocate for this session is idempotent and a
                // ReleaseForDirector can clean it up; the number simply is not exclusively ours.
                pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
                return;
            }

            pool.InUse.Add(number);
            pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} <- {sessionId} (director={directorId}, {pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>
    /// Free the number owned by <paramref name="sessionId"/> IN <paramref name="tenant"/>'s partition back to
    /// that tenant's pool when its session ends. The number may later be reused. Releasing an unknown session
    /// (or an unknown tenant) is a harmless no-op.
    /// </summary>
    public void Release(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock)
        {
            if (!_byTenant.TryGetValue(tenant, out var pool)) return;
            if (!pool.BySession.Remove(sessionId, out var a))
                return;
            // Only clear the shared reservation if no other session still holds this number (a
            // pre-existing offline collision could have two sessions on one number).
            if (!pool.BySession.Values.Any(x => x.Number == a.Number))
                pool.InUse.Remove(a.Number);
            FileLog.Write($"[FleetSessionNumberAllocator] Release: freed {a.Number} from {sessionId} ({pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>
    /// Free every number owned by <paramref name="directorId"/> IN <paramref name="tenant"/>'s partition.
    /// Called when a Director is removed from the registry (graceful unregister, or swept after its heartbeat
    /// went stale / its endpoint stayed unreachable past the evict window) - a Director that died without
    /// releasing its sessions' numbers. This is tied to the registry's own liveness decision, so it never
    /// fires for a Director that is merely momentarily unreachable.
    ///
    /// TENANT-SCOPED (audit H2). A director id is unique only within its tenant, so the tenant MUST be
    /// supplied - <see cref="DirectorRegistry.OnDirectorRemoved"/> carries the owning tenant in its
    /// <see cref="DirectorRemoval"/> payload, and the subscriber threads it straight through here. Only the
    /// named tenant's partition is touched, so one tenant's removal can never free another tenant's numbers.
    /// </summary>
    public void ReleaseForDirector(TenantId tenant, string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        lock (_lock)
        {
            if (!_byTenant.TryGetValue(tenant, out var pool)) return;
            var gone = pool.BySession.Where(kv => string.Equals(kv.Value.DirectorId, directorId, StringComparison.Ordinal))
                                     .Select(kv => kv.Key).ToList();
            foreach (var sid in gone)
                pool.BySession.Remove(sid);
            // Recompute the reserved set from what remains, so numbers shared by a surviving session stay held.
            pool.InUse.Clear();
            foreach (var a in pool.BySession.Values)
                pool.InUse.Add(a.Number);
            if (gone.Count > 0)
                FileLog.Write($"[FleetSessionNumberAllocator] ReleaseForDirector {directorId}: freed {gone.Count} number(s) ({pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>Lowest free number in [lo, hi] within the pool, or null when the whole band is in use. Caller holds the lock.</summary>
    private static int? FirstFree(TenantPool pool, int lo, int hi)
    {
        for (int n = lo; n <= hi; n++)
            if (!pool.InUse.Contains(n))
                return n;
        return null;
    }
}
