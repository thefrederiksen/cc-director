using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// A supervised session's "I need my supervisor" hand, held by the Gateway (issue #2662).
///
/// WHY THIS EXISTS. Suppressing a supervised session's attention is only safe while something else is
/// listening. Until this, nothing was: <c>SessionDto.NeedsManager</c> was declared on the wire and had zero
/// writers and zero readers, so a worker that was genuinely blocked had no way to say so - it no longer
/// reached the owner (by design) and it could not reach its supervisor either (by omission). That is not
/// attention routed elsewhere, it is attention lost, and it is the half that makes the quiet safe.
///
/// WHY IT IS THE GATEWAY THAT HOLDS IT. The same reason the role is: a worker's supervisor may be a session
/// on another machine, so no single Director can answer "which of MY workers wants me?". The raise is
/// recorded here and folded onto the roster every Director, Cockpit and phone already reads.
///
/// IN MEMORY, AND THAT IS A DECISION RATHER THAN A SHORTCUT. A raised hand is a live conversation between
/// two live sessions and it is short-lived by construction - the fold clears it the moment the worker stops
/// working, which is usually minutes. A snooze is DB-backed because it is a promise about the future that
/// must outlive a restart; a raised hand is a fact about right now. If the Gateway restarts, the worker is
/// still there, still blocked, and raises again on its next turn. Persisting it would buy a stale hand
/// pointing at a decision that has probably already been made.
///
/// TENANT-PARTITIONED, like every other piece of Gateway session state: one account can never see or clear
/// another's raised hands.
/// </summary>
public sealed class HandRaiseRegistry
{
    /// <summary>One raised hand: what the worker needs, and when it asked.</summary>
    /// <param name="Reason">The worker's own words - what decision it is blocked on. Never composed by us.</param>
    /// <param name="RaisedAtUtc">When it asked, so a supervisor can tell a fresh ask from a stale one.</param>
    public sealed record RaisedHand(string Reason, DateTime RaisedAtUtc);

    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, RaisedHand>> _byTenant = new();
    private readonly Func<DateTime> _utcNow;

    public HandRaiseRegistry(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    private ConcurrentDictionary<string, RaisedHand> For(TenantId tenant) =>
        _byTenant.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, RaisedHand>(StringComparer.Ordinal));

    /// <summary>
    /// Record that this session needs its supervisor, with the worker's own words for what it is blocked on.
    /// Raising again REPLACES the previous reason rather than stacking - a worker has one current blocker,
    /// and the newest statement of it is the true one.
    /// </summary>
    /// <returns>The recorded raise.</returns>
    public RaisedHand Raise(TenantId tenant, string sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
        var entry = new RaisedHand((reason ?? "").Trim(), _utcNow());
        For(tenant)[sessionId] = entry;
        return entry;
    }

    /// <summary>
    /// Lower the hand - the decision was answered. Idempotent: clearing a hand that is not raised is a
    /// success, not an error, because the fold also lowers hands on its own (see <see cref="Get"/>) and a
    /// supervisor answering a worker must never fail because the worker got there first.
    /// </summary>
    /// <returns>True when a raise was actually removed.</returns>
    public bool Clear(TenantId tenant, string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && For(tenant).TryRemove(sessionId, out _);

    /// <summary>The raise for this session, or null when its hand is down.</summary>
    public RaisedHand? Get(TenantId tenant, string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : For(tenant).TryGetValue(sessionId, out var r) ? r : null;

    /// <summary>True when this session's hand is up. Convenience over <see cref="Get"/>.</summary>
    public bool IsRaised(TenantId tenant, string sessionId) => Get(tenant, sessionId) is not null;

    /// <summary>
    /// Drop raises for sessions that are no longer live. Housekeeping only - the FOLD is what stops a stale
    /// hand being shown (it lowers any hand on a session that is not working), so this never affects what a
    /// supervisor sees. It exists so a long-running Gateway does not accumulate entries for sessions that
    /// ended months ago.
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int PruneNotLive(TenantId tenant, ISet<string> liveSessionIds)
    {
        if (liveSessionIds is null) throw new ArgumentNullException(nameof(liveSessionIds));
        var map = For(tenant);
        var dropped = 0;
        foreach (var sid in map.Keys)
        {
            if (!liveSessionIds.Contains(sid) && map.TryRemove(sid, out _)) dropped++;
        }
        return dropped;
    }
}
