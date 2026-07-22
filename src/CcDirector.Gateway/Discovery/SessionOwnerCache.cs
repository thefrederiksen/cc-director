using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// Remembers which Director last owned each session id, so the per-session WebSocket proxy can tell
/// "unknown session" (404) apart from "known session whose owning Director has gone offline /
/// unreachable" (503).
///
/// Issue #288: live ownership resolution (the <c>GetSessionAsync</c> fan-out in
/// <see cref="Api.SessionWsProxyEndpoints"/>) only confirms ownership by reaching the owning
/// Director, so an OFFLINE Director makes resolution fail and every per-session WS request collapse
/// to 404 - contradicting issue #268's AC4 ("offline owning Director -> 503"). This cache breaks the
/// tie: the fleet aggregator (<c>GET /sessions</c>) records every session it observes, and the WS
/// proxy records every session it successfully forwards, so any session the Cockpit has seen
/// resolves to a 503 - not a 404 - once its Director goes dark.
///
/// Hosted Multi-Tenancy (audit gap audit-a/f): the cache is partitioned by
/// <c>(tenant, sessionId)</c>. Every write, read, and prune only ever touches the caller's OWN tenant
/// partition, so tenant A can never overwrite tenant B's cached owner (colliding session id) and
/// tenant A's <see cref="RetainForDirector"/> can never evict tenant B's retained entry for a Director
/// id the two tenants happen to share. On self-host every caller passes <see cref="TenantId.Local"/>,
/// so this is a single partition and behavior is identical to before. The tenant always comes from the
/// server-resolved request context, never from client input.
///
/// In-memory only; it rebuilds from the next aggregation after a Gateway restart. Session ids are
/// GUIDs and never reused, so a stale entry can only point at a Director that is itself gone, which
/// is exactly the 503 case. Bounded in practice by the number of sessions the fleet has run.
/// </summary>
public sealed class SessionOwnerCache
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), string> _ownerBySession =
        new(new KeyComparer());

    /// <summary>Record (or refresh) the Director that owns <paramref name="sessionId"/> within
    /// <paramref name="tenant"/>. No-op on an invalid tenant or empty input.</summary>
    public void Remember(TenantId tenant, string sessionId, string directorId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(directorId)) return;
        _ownerBySession[(tenant, sessionId)] = directorId;
    }

    /// <summary>The Director id last seen owning <paramref name="sessionId"/> within
    /// <paramref name="tenant"/>, or null if never observed for that tenant.</summary>
    public string? OwnerOf(TenantId tenant, string sessionId)
        => tenant.IsValid && !string.IsNullOrEmpty(sessionId) && _ownerBySession.TryGetValue((tenant, sessionId), out var id) ? id : null;

    /// <summary>
    /// Drop the cached owner for <paramref name="sessionId"/> within <paramref name="tenant"/> (e.g. the
    /// session has exited). No-op when the tenant is invalid or the id is empty or not cached.
    /// </summary>
    public void Forget(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(sessionId)) return;
        _ownerBySession.TryRemove((tenant, sessionId), out _);
    }

    /// <summary>
    /// Reconcile the cache against a REACHABLE Director's live session set (issue #291): drop every
    /// cached entry IN <paramref name="tenant"/>'s partition that points at <paramref name="directorId"/>
    /// but whose session id is no longer in <paramref name="liveSessionIds"/>. A session is considered
    /// gone when the Director (which we just reached) no longer reports it as live - it exited or
    /// disappeared - so the per-session WS proxy reverts to 404 instead of the 503 "owner offline"
    /// verdict from #288.
    ///
    /// Caller contract: only invoke this for a Director that just answered (reachable) UNDER
    /// <paramref name="tenant"/>. Entries owned by a DIFFERENT Director are never touched, so an offline
    /// owner's sessions stay cached -> still 503 (#288 must not regress). Entries in a DIFFERENT tenant's
    /// partition are never touched even when that tenant shares the same Director id, so a reconcile in
    /// one tenant can never evict another tenant's retained state (audit gap audit-a/f).
    /// </summary>
    public void RetainForDirector(TenantId tenant, string directorId, IReadOnlyCollection<string> liveSessionIds)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(directorId)) return;

        var live = liveSessionIds as HashSet<string> ?? new HashSet<string>(liveSessionIds, StringComparer.Ordinal);
        foreach (var kvp in _ownerBySession)
        {
            if (!kvp.Key.Tenant.Equals(tenant)) continue;
            if (!string.Equals(kvp.Value, directorId, StringComparison.Ordinal)) continue;
            if (live.Contains(kvp.Key.SessionId)) continue;
            _ownerBySession.TryRemove(new KeyValuePair<(TenantId, string), string>(kvp.Key, kvp.Value));
        }
    }

    // Session ids are compared case-sensitively (they are GUIDs); the tenant half uses TenantId's own
    // value equality. Matches the pre-partition Ordinal comparison so behavior is unchanged per tenant.
    private sealed class KeyComparer : IEqualityComparer<(TenantId Tenant, string SessionId)>
    {
        public bool Equals((TenantId Tenant, string SessionId) x, (TenantId Tenant, string SessionId) y)
            => x.Tenant.Equals(y.Tenant) && string.Equals(x.SessionId, y.SessionId, StringComparison.Ordinal);

        public int GetHashCode((TenantId Tenant, string SessionId) obj)
            => HashCode.Combine(obj.Tenant, StringComparer.Ordinal.GetHashCode(obj.SessionId));
    }
}
