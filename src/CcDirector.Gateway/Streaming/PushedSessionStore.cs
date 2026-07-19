using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1176 (Phase 1a): the Gateway's in-memory cache of the session state each Director pushes UP
/// over its persistent stream, replacing the on-demand pull for stream-connected Directors. One entry
/// per Director (keyed by directorId), holding the sessions that Director last pushed.
///
/// Tenant partitioning (Stage 3b of the multi-tenant plan): entries are partitioned by
/// <see cref="TenantId"/> first, then by directorId. A Director belongs to exactly one tenant, and the
/// cross-tenant scans (<see cref="TryLocate"/>, <see cref="SnapshotFresh"/>) only ever walk one
/// tenant's partition, so a session id from one tenant can never resolve a Director in another. Today the
/// core is single-tenant: callers pass <see cref="TenantId.Local"/> and behavior is unchanged. When the
/// resolver lands, the tenant is the one resolved for the Director's connection (ingest) or the request
/// (reads); nothing in this store changes.
///
/// Correctness rules this store enforces (Phase 1 plan, section 4.4):
///  - CONNECTION GENERATION: each stream connection is identified by its SignalR connection id. A new
///    connection becomes the active connection for its Director and its first message is authoritative.
///    This is what lets a RESTARTED Director - which dials a brand-new connection - reseed even though a
///    prior connection had pushed higher sequence numbers (a plain persistent integer epoch would wrongly
///    reject the restarted Director's snapshot).
///  - SEQUENCE ORDERING: within one connection, messages carry a monotonic sequence; a message with a
///    sequence at or below the last applied one is dropped as stale/duplicate.
///  - ACTIVE-CONNECTION OWNERSHIP: snapshot/delta/remove are accepted only from the currently active
///    connection. A late disconnect from a superseded connection (a reconnect overlap) does NOT clear the
///    active connection or the cache.
///  - SNAPSHOT IS AUTHORITATIVE: a full snapshot replaces the Director's whole session set, pruning any
///    session not present in it.
///
/// Reads (<see cref="TryGetFresh"/>) return deep COPIES so the aggregation may stamp them without
/// contaminating the cache, and recompute relative time fields (idle seconds) from the absolute
/// LastActivityAt so a quiet session's idle clock keeps advancing between pushes.
///
/// Thread-safe: a <see cref="ConcurrentDictionary{TKey,TValue}"/> per tenant of per-Director entries,
/// each entry guarded by its own lock.
/// </summary>
public sealed class PushedSessionStore
{
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, DirectorEntry>> _byTenant = new();
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    /// Create the store. <paramref name="utcNow"/> is a test seam for the staleness and idle-clock logic;
    /// production passes null and the store reads <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public PushedSessionStore(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The per-Director map for one tenant, created on first use. Fails loud on an invalid tenant.</summary>
    private ConcurrentDictionary<string, DirectorEntry> DirectorsFor(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        return _byTenant.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, DirectorEntry>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The tenants that currently have partitions in the store (Hosted Multi-Tenancy, session-serving). A
    /// background loop that must act across all tenants - the fold, the role/display sweeps - iterates THESE
    /// on the hosted Gateway (entering each tenant's scope in turn) instead of a single Local pass, and
    /// without a per-tick database scan. A snapshot of the live keys; a tenant that appears or disappears
    /// between calls is picked up on the next sweep, which is the same eventual-consistency the sweeps already
    /// rely on. On self-host this is just the single Local partition.
    /// </summary>
    public IReadOnlyCollection<TenantId> KnownTenants() => _byTenant.Keys.ToArray();

    /// <summary>
    /// The director ids that have a partition entry under <paramref name="tenant"/> (Hosted Multi-Tenancy,
    /// session-serving). A director enters a tenant's partition when it binds a stream connection under that
    /// tenant (<see cref="RegisterConnection"/>) and STAYS after it disconnects (the entry is retained so a
    /// reconnect keeps roster continuity). So this is exactly "the directors this tenant owns" - the set a
    /// tenant's roster read must scope its Director list to on the hosted Gateway, so a request never even
    /// NAMES another tenant's directors: not as sessions, and not as "unreachable" machineError / reachability
    /// rows (which would otherwise leak another account's director ids and machine names). On self-host the
    /// registry already IS the single Local tenant's directors, so the roster does not consult this.
    /// </summary>
    public IReadOnlyCollection<string> DirectorIdsFor(TenantId tenant) => DirectorsFor(tenant).Keys.ToArray();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for <paramref name="directorId"/>
    /// within <paramref name="tenant"/>. Existing cached sessions are kept (so a fast reconnect keeps roster
    /// continuity), but the sequence baseline resets so the new connection's first message - at any sequence -
    /// is authoritative. The last-received timestamp is deliberately NOT refreshed here: until the new
    /// connection actually pushes a snapshot, the cache is treated as stale so aggregation falls back to pull.
    /// </summary>
    public void RegisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        var entry = DirectorsFor(tenant).GetOrAdd(directorId, _ => new DirectorEntry());
        lock (entry.Gate)
        {
            entry.ActiveConnectionId = connectionId;
            entry.LastSequence = -1;
        }
        FileLog.Write($"[PushedSessionStore] RegisterConnection: tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)} is now the active connection");
    }

    /// <summary>
    /// Clear the active connection for <paramref name="directorId"/> IF <paramref name="connectionId"/> is
    /// still the active one. A late disconnect from a superseded connection is logged and ignored so it
    /// cannot wipe a newer active connection. Cached sessions are retained; while no connection is active,
    /// <see cref="TryGetFresh"/> returns null so aggregation pulls instead.
    /// </summary>
    /// <returns>
    /// True when this call actually cleared the active connection (so the Director now has NO live stream);
    /// false when it was ignored because a newer connection is active (a reconnect superseded this one) or
    /// the Director is unknown. Gateway Cleanup mission (tunnel-only): the caller uses this to decide whether
    /// to drop the Director from the registry - only a genuine last-connection close should.
    /// </returns>
    public bool UnregisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedSessionStore] UnregisterConnection IGNORED (not active): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
                return false;
            }
            entry.ActiveConnectionId = null;
        }
        FileLog.Write($"[PushedSessionStore] UnregisterConnection: tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)} cleared; aggregation will fall back to pull");
        return true;
    }

    /// <summary>
    /// Apply a full snapshot: replace the Director's whole session set, pruning any session not present.
    /// </summary>
    /// <returns>true if applied; false if rejected (not the active connection, or a stale sequence).</returns>
    public bool ApplySnapshot(TenantId tenant, string directorId, string connectionId, long sequence, IReadOnlyList<SessionDto> sessions)
    {
        if (sessions is null)
            throw new ArgumentNullException(nameof(sessions));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "snapshot"))
                return false;

            entry.Sessions.Clear();
            foreach (var s in sessions)
            {
                if (!string.IsNullOrEmpty(s.SessionId))
                {
                    DiscardInboundRole(s);
                    entry.Sessions[s.SessionId] = s;
                }
            }
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>Apply a single-session delta: upsert one session.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyDelta(TenantId tenant, string directorId, string connectionId, long sequence, SessionDto session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(session.SessionId))
            throw new ArgumentException("session.SessionId is required", nameof(session));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "delta"))
                return false;

            DiscardInboundRole(session);
            entry.Sessions[session.SessionId] = session;
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>
    /// Defect 5 - THE INGEST DISCARD. A Director's pushed session carries whatever
    /// <see cref="SessionDto.SessionRole"/> the Gateway last stamped down onto it (the Director echoes the
    /// role back up on its next delta, because it reports its whole session through one mapper). That echo
    /// is NOT an authority and must never be mistaken for one, so it dies here, at the boundary, before it
    /// can be read by anything.
    ///
    /// WHY THIS IS NOT BELT-AND-BRACES. Without it, "the Gateway is the only thing that computes the role"
    /// is true only by luck of statement ordering - it holds today purely because
    /// <see cref="Fleet.FleetRoleResolver.Stamp"/> assigns on every branch, so the echoed value happens to
    /// be overwritten before anything reads it. That is an accident, not a design. The moment someone adds
    /// a <c>?? s.SessionRole</c> to "preserve" a role the resolver could not determine, a Director's stale
    /// echo becomes the answer, and the whole defect class re-opens - silently, because the value will look
    /// correct almost always. Nulling it here makes the guarantee structural: a role that was not computed
    /// by this Gateway, on this pass, does not exist.
    ///
    /// Note the read paths that skip the fleet pass (the Exes page, GET /sessions/{sid}) now correctly
    /// report null rather than a stale echo - null means "nobody has resolved this", which is the truth.
    /// </summary>
    private static void DiscardInboundRole(SessionDto session) => session.SessionRole = null;

    /// <summary>Apply a remove/tombstone: drop one session from the Director's set.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyRemove(TenantId tenant, string directorId, string connectionId, long sequence, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "remove"))
                return false;

            entry.Sessions.Remove(sessionId);
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>
    /// Return deep COPIES of the Director's cached sessions when the stream is connected and the last push
    /// is within <paramref name="staleAfter"/>; otherwise null so the caller pulls. Copies are returned so
    /// the /sessions aggregation may stamp them without contaminating the cache. Relative time fields (idle
    /// seconds) are recomputed from the absolute LastActivityAt at serve time so a quiet session's idle
    /// clock keeps advancing between pushes.
    /// </summary>
    public IReadOnlyList<SessionDto>? TryGetFresh(TenantId tenant, string directorId, TimeSpan staleAfter)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return null;

        lock (entry.Gate)
        {
            if (entry.ActiveConnectionId is null)
                return null;
            if (entry.ReceivedAtUtc == DateTime.MinValue)
                return null;

            var now = _utcNow();
            if (now - entry.ReceivedAtUtc > staleAfter)
                return null;

            var copies = new List<SessionDto>(entry.Sessions.Count);
            foreach (var s in entry.Sessions.Values)
                copies.Add(RecomputeClocks(s.Clone(), now));
            return copies;
        }
    }

    /// <summary>
    /// Issue #1177 (Phase 4a): find the Director that currently owns <paramref name="sessionId"/> in a FRESH
    /// pushed cache, without any HTTP pull. Scans <paramref name="tenant"/>'s Directors only (each under its
    /// own lock) and returns the first fresh match as (directorId, deep-copied session). Returns null when no
    /// stream-connected Director in this tenant holds the session, which post-cut means the session cannot be
    /// located at all - the pushed cache is the ONLY roster source, so the caller reports "not found" rather
    /// than dialling anything.
    ///
    /// This is the portless session-location primitive: a remotely-unreachable Director has an empty control
    /// endpoint, so an HTTP pull can never locate its sessions, but the pushed cache already records which
    /// Director pushed each session. Freshness and idle-clock recompute follow the same rules as
    /// <see cref="TryGetFresh"/>. Because it only walks one tenant's partition, a session id can never
    /// resolve a Director in a different tenant.
    /// </summary>
    public (string DirectorId, SessionDto Session)? TryLocate(TenantId tenant, string sessionId, TimeSpan staleAfter)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var now = _utcNow();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is null)
                    continue;
                if (entry.ReceivedAtUtc == DateTime.MinValue)
                    continue;
                if (now - entry.ReceivedAtUtc > staleAfter)
                    continue;
                if (entry.Sessions.TryGetValue(sessionId, out var s))
                    return (kvp.Key, RecomputeClocks(s.Clone(), now));
            }
        }
        return null;
    }

    /// <summary>
    /// Issue #1200 (auto-dismiss sweep): enumerate every FRESH pushed session across <paramref name="tenant"/>'s
    /// stream-connected Directors, each paired with its owning directorId so the caller can address a command
    /// DOWN that Director's stream. Applies the same active-connection + freshness rules as
    /// <see cref="TryGetFresh"/> and returns deep COPIES with recomputed idle clocks, so a caller may inspect
    /// them without touching the cache. A Director with no active connection or a stale cache contributes
    /// nothing. Scoped to one tenant - a sweep for one tenant never sees another's sessions.
    /// </summary>
    public IReadOnlyList<(string DirectorId, SessionDto Session)> SnapshotFresh(TenantId tenant, TimeSpan staleAfter)
    {
        var now = _utcNow();
        var result = new List<(string, SessionDto)>();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is null)
                    continue;
                if (entry.ReceivedAtUtc == DateTime.MinValue)
                    continue;
                if (now - entry.ReceivedAtUtc > staleAfter)
                    continue;
                foreach (var s in entry.Sessions.Values)
                    result.Add((kvp.Key, RecomputeClocks(s.Clone(), now)));
            }
        }
        return result;
    }

    /// <summary>True when this Director currently has an active stream connection (used for diagnostics).</summary>
    public bool IsStreamConnected(TenantId tenant, string directorId) =>
        DirectorsFor(tenant).TryGetValue(directorId, out var entry) && entry.ActiveConnectionId is not null;

    /// <summary>
    /// The active stream connection id for a Director, or null when none. The Gateway uses it to address a
    /// message DOWN the stream to that Director (issue #1176, Phase 1b down-channel).
    /// </summary>
    public string? GetActiveConnectionId(TenantId tenant, string directorId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return null;
        lock (entry.Gate)
            return entry.ActiveConnectionId;
    }

    private static bool IsAcceptable(DirectorEntry entry, TenantId tenant, string directorId, string connectionId, long sequence, string kind)
    {
        if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (not the active connection): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
            return false;
        }
        if (sequence <= entry.LastSequence)
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (stale sequence {sequence} <= last {entry.LastSequence}): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}");
            return false;
        }
        return true;
    }

    private static SessionDto RecomputeClocks(SessionDto s, DateTime nowUtc)
    {
        if (s.LastActivityAt is DateTime last)
        {
            var idle = (nowUtc - last).TotalSeconds;
            s.IdleSeconds = idle > 0 ? idle : 0;
        }
        return s;
    }

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);

    private sealed class DirectorEntry
    {
        public readonly object Gate = new();
        public string? ActiveConnectionId;
        public long LastSequence = -1;
        public DateTime ReceivedAtUtc = DateTime.MinValue;
        public readonly Dictionary<string, SessionDto> Sessions = new(StringComparer.Ordinal);
    }
}
