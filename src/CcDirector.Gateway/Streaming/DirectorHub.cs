using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.SignalR;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1176 (Phase 1a): the SignalR hub each Director dials OUT to. It receives the session state a
/// Director pushes UP - a full snapshot on connect/reconnect, per-session deltas, and removes - and
/// records it in the <see cref="PushedSessionStore"/> so the <c>/sessions</c> aggregation can serve that
/// Director from cache instead of pulling it.
///
/// Identity binding (review #9): the first message MUST be <see cref="Hello"/>, which binds this
/// connection to one Director id. Every later message uses that bound id - never a Director id re-sent by
/// the client - so a connection can only ever affect the Director it declared, and a message that arrives
/// before Hello is rejected. The transport itself is already authenticated by the host-wide token
/// middleware (a valid shared token or per-device key); binding a credential cryptographically to a
/// specific Director id is future work that rides the account/device epic.
///
/// The hub holds no state of its own; it is a thin adapter onto <see cref="PushedSessionStore"/> and
/// <see cref="DirectorRegistry"/>. SignalR constructs it per invocation via dependency injection (the
/// one place this codebase uses a container, because the framework requires it).
/// </summary>
public sealed class DirectorHub : Hub
{
    private const string DirectorIdItemKey = "cc.directorId";
    private const string TenantIdItemKey = "cc.tenantId";

    private readonly PushedSessionStore _store;
    private readonly DirectorRegistry _registry;
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly GatewayStreamRegistry _streamRegistry;
    private readonly Snooze.SnoozeLandingObserver? _snoozeLandings;
    private readonly Fleet.FleetRoleObserver? _fleetRoles;
    private readonly Fleet.FleetDisplayStateObserver? _fleetDisplayState;
    // Hosted Multi-Tenancy increment 1: resolves THIS connection's tenant from the authenticated device key
    // at Hello and enters that tenant's scope on every push (so the EF-writing observers stamp the right
    // tenant). Null for older callers/tests -> the self-host Local behavior, unchanged.
    private readonly HostedTenantBoundary? _tenantBoundary;

    private readonly DirectorConnectionRegistry? _connections;

    // The statistics WRITE queue. The ingress never writes to a statistics store; it offers the work here
    // and returns. This moved out of the GET /sessions handler after the 2026-07-30 outage, where a
    // corrupted statistics database threw out of the roster read and answered 500 to every client for 32
    // minutes - and it is a QUEUE rather than a try/catch because containing a throw does nothing about a
    // stall, and these stores sit on a network share where a write can hang instead of failing.
    private readonly Stats.StatisticsObservationQueue? _statsQueue;
    // The session-number allocator. NOT a statistics collaborator and deliberately not treated as one: it is
    // dictionaries under per-partition locks with no database context and no file, so it cannot hang on the
    // share or raise a storage error, and a DROPPED adoption would let the allocator re-issue a number that
    // is still live (issue #1292 returning as duplicate session numbers). It stays inline and loud.
    private readonly Discovery.FleetSessionNumberAllocator? _sessionNumbers;

    public DirectorHub(PushedSessionStore store, DirectorRegistry registry, GatewayInputStatsAggregator inputStats,
        GatewayStreamRegistry streamRegistry, Snooze.SnoozeLandingObserver? snoozeLandings = null,
        Fleet.FleetRoleObserver? fleetRoles = null, Fleet.FleetDisplayStateObserver? fleetDisplayState = null,
        HostedTenantBoundary? tenantBoundary = null, DirectorConnectionRegistry? connections = null,
        PushedRepositoryStore? repositoryStore = null, RepoHistoryStore? repoHistory = null,
        History.SessionHistoryRecorder? sessionHistory = null,
        Discovery.FleetSessionNumberAllocator? sessionNumbers = null,
        Stats.StatisticsObservationQueue? statsQueue = null)
    {
        _store = store;
        _registry = registry;
        _inputStats = inputStats;
        _streamRegistry = streamRegistry;
        _snoozeLandings = snoozeLandings;
        _fleetRoles = fleetRoles;
        _fleetDisplayState = fleetDisplayState;
        _tenantBoundary = tenantBoundary;
        _connections = connections;
        _repositoryStore = repositoryStore;
        _repoHistory = repoHistory;
        _sessionHistory = sessionHistory;
        _sessionNumbers = sessionNumbers;
        _statsQueue = statsQueue;
    }

    /// <summary>
    /// Claim every session number in <paramref name="sessions"/> into this tenant's in-use set (issue #1292),
    /// so the allocator never re-issues a number a Director assigned offline or one still live across a
    /// Gateway restart.
    ///
    /// DELIBERATELY NOT WRAPPED, and deliberately not treated as a statistic. The allocator is dictionaries
    /// under per-partition locks - no database context, no file, nothing that can hang on the share or raise
    /// a storage error - so the containment the statistics writes need does not apply here, and applying it
    /// anyway would be worse than useless: swallowing a failure would let a number that is still live be
    /// handed out again, which is issue #1292 returning as duplicate session numbers that nobody would trace
    /// back to this change. A throw here is a real defect in pure in-memory code and should be loud.
    /// </summary>
    private void AdoptNumbers(string directorId, IReadOnlyList<SessionDto> sessions)
    {
        if (_sessionNumbers is null) return;
        var tenant = RequireBoundTenant();
        foreach (var s in sessions)
            if (s?.Number is int num && !string.IsNullOrEmpty(s.SessionId))
                _sessionNumbers.Adopt(tenant, s.SessionId, directorId, num);
    }

    private readonly PushedRepositoryStore? _repositoryStore;
    private readonly RepoHistoryStore? _repoHistory;
    // Issue #2194: the durable work-history recorder, fed from the same accepted pushes as the other
    // observers. Throttled internally (it is NOT a write per push) and never throws.
    private readonly History.SessionHistoryRecorder? _sessionHistory;

    /// <summary>
    /// A full repository/worktree snapshot from the bound Director (repositories mission, #510
    /// phase C). Snapshots only; tenant comes from the connection binding, never the payload.
    /// Accepted pushes also fold into the daily history (phase D) - rejected/stale ones never do.
    /// </summary>
    public void PushRepoSnapshot(long sequence, RepoStatusDto[] repositories)
    {
        var directorId = RequireBoundDirector();
        var set = repositories ?? Array.Empty<RepoStatusDto>();
        var accepted = _repositoryStore?.ApplySnapshot(RequireBoundTenant(), directorId, Context.ConnectionId,
            sequence, set) ?? false;
        if (accepted)
            _repoHistory?.ObserveSnapshot(RequireBoundTenant(), directorId, set);
        FileLog.Write($"[DirectorHub] PushRepoSnapshot: director={directorId} seq={sequence} repos={set.Length} accepted={accepted}");
    }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the Director streams a byte/frame stream UP under the
    /// stream id the Gateway minted when it opened the stream over the tunnel. This is native client-to-server
    /// streaming (the Director is the SignalR client). The registry pumps the frames into the browser-facing
    /// sink with pull-then-forward backpressure, so a slow browser blocks the pull, which - with a small
    /// StreamBufferCapacity - blocks the Director's producer. One primitive serves both the live terminal and a
    /// finite file/screenshot read (keyed by the stream id). Requires the connection be bound first (Hello).
    ///
    /// Issue #1923: binding proves WHO is calling; it does not prove the caller owns THIS stream. The bound
    /// identity (tenant resolved from the authenticated device key at Hello, plus the bound Director id) is
    /// handed to the registry, which authorizes it against the owner recorded when the stream was opened and
    /// REFUSES a caller that does not match. Without that, any authenticated account that learned or guessed a
    /// live stream id could write frames into another account's terminal, claim the stream ahead of the real
    /// Director, or tear it down.
    /// </summary>
    public async Task StreamUp(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames)
    {
        var directorId = RequireBoundDirector();
        var caller = new StreamOwner(RequireBoundTenant(), directorId);
        FileLog.Write($"[DirectorHub] StreamUp: director={directorId}, stream={streamId}, conn={Short(Context.ConnectionId)}");
        try
        {
            await _streamRegistry.ConsumeAsync(streamId, caller, frames, Context.ConnectionAborted);
        }
        catch (StreamOwnershipDeniedException ex)
        {
            // Surface the refusal to the calling Director as a hub error (HubException is the one exception
            // type SignalR relays verbatim). A refusal is never swallowed: an operator reading either side's
            // log must be able to tell a cross-account injection attempt from an ordinary closed-stream race.
            FileLog.Write($"[DirectorHub] StreamUp REFUSED: director={directorId}, stream={streamId}, conn={Short(Context.ConnectionId)}");
            throw new HubException(ex.Message);
        }
    }

    /// <summary>Bind this connection to a Director. Must be the first message; aborts the connection on a bad id.</summary>
    public void Hello(DirectorStreamHello hello)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.DirectorId))
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (missing directorId): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        var directorId = hello.DirectorId.Trim();
        var alreadyBound = BoundDirectorId();
        if (alreadyBound is not null && !string.Equals(alreadyBound, directorId, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (conn already bound to {alreadyBound}, cannot re-claim {directorId}): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        // Hosted Multi-Tenancy increment 1: resolve the tenant this connection belongs to ONCE, here at bind
        // time, from the AUTHENTICATED device key the auth layer stashed on the negotiate request - NEVER from
        // the Hello payload. On self-host (or no boundary) this is Local, unchanged. On hosted a device key
        // with no bound tenant is a DENY: abort rather than bind a wrong or defaulted tenant (deny-by-default).
        // THIS resolution is the isolation line - reverting it to a fixed tenant makes two accounts share one.
        var resolved = ResolveConnectionTenant();
        if (resolved is not { } tenant)
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (hosted: the authenticated device key resolves to no tenant): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }
        Context.Items[DirectorIdItemKey] = directorId;
        Context.Items[TenantIdItemKey] = tenant;
        _store.RegisterConnection(tenant, directorId, Context.ConnectionId);
        // The repository store follows the same ownership discipline: only this - the current -
        // connection may push repository snapshots from now on.
        _repositoryStore?.RegisterConnection(tenant, directorId, Context.ConnectionId);
        // MTR-15 cancellation cutoff: index this live tunnel by tenant with a server-side abort, so a revoked
        // tenant's connection is severed the moment the sweep (or a request re-read) finds it NotEntitled. The
        // durable device tombstone denies NEW auth; this ends the tunnel already up. Cleared on disconnect.
        var abortContext = Context;
        _connections?.Register(tenant, Context.ConnectionId, () => abortContext.Abort());
        // Gateway Cleanup mission (tunnel-only): the stream IS the registration now (HTTP register is gone).
        // Register this Director from the Hello identity so registry.Get(tenant, id) - the gate on create-session
        // and the other director-level routes - resolves it. Source="stream", no dialable endpoint.
        // Issue #1847: register it UNDER THE RESOLVED TENANT. The tenant is half of the registry key, so this
        // Hello can only create or refresh THIS account's own entry - naming another account's Director is
        // structurally impossible, however the client chose hello.DirectorId. It also makes the entry visible
        // to this account's /directors list and to no other. The tenant is the one resolved above from the
        // authenticated device key - never the Hello payload, which the client writes.
        _registry.RegisterFromStream(directorId, hello.MachineName, hello.User, hello.Version, hello.Pid, hello.StartedAt, tenant);
        FileLog.Write($"[DirectorHub] Hello: director={directorId} bound to conn={Short(Context.ConnectionId)} (version={hello.Version}, machine={hello.MachineName})");
    }

    /// <summary>A full snapshot: replaces the bound Director's session set (pruning anything absent).</summary>
    public void PushSnapshot(long sequence, SessionDto[] sessions)
    {
        var directorId = RequireBoundDirector();
        // Hosted Multi-Tenancy increment 1: run the whole handler in the bound tenant's scope, so the
        // EF-writing observers below (snooze landings, spend) stamp and filter by this connection's tenant.
        using var tenantScope = EnterBoundTenantScope();
        var set = sessions ?? Array.Empty<SessionDto>();
        var accepted = _store.ApplySnapshot(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, set);
        // DevThrottle Stats: fold each session's input tally into the always-available aggregate, under this
        // connection's bound tenant (MTR-08) so one account's tallies never coalesce with another's.
        // OFFERED, NOT CALLED. The ingress does not write to a store: it hands the work to the bounded
        // queue and returns. A try/catch here would contain a throw but not a STALL, and these stores live
        // on a network share where a write can hang - which would hold this hub thread and, through the
        // concurrency store's lock, every other one. See StatisticsObservationQueue.
        var snapshotTenant = RequireBoundTenant();
        _statsQueue?.Offer(Stats.StatisticsObservationQueue.InputStatsObserver,
            _ => { _inputStats.ObserveSnapshot(set, tenant: snapshotTenant); return Task.CompletedTask; });
        // Fleet concurrency and the hourly activity log - max concurrent live and actively-working sessions,
        // plus the distinct sessions/machines/repositories active each hour. MOVED here from the GET /sessions
        // handler: the tracker keeps only the higher value per hour, so observing every push is idempotent in
        // exactly the way observing every read was, and it no longer depends on a client polling the roster.
        // CONCURRENCY IS NOT OBSERVED HERE, AND MUST NOT BE ADDED BACK. It is TIMER-SAMPLED in GatewayHost
        // instead. Observing it per push meant every hub thread contending on the concurrency store's single
        // lock, which that store holds across a synchronous write to the shared file - so share latency would
        // not stall one push, it would convoy the entire ingress and take the roster stale fleet-wide.
        // Sampling is also the honest shape for a high-water measure: nobody needs the peak recomputed on
        // every delta, they need it observed regularly.
        //
        // Issue #1292: adopt every observed number into the allocator's in-use set, so the Gateway never
        // re-issues a number a Director assigned offline or one still live after a Gateway restart.
        //
        // GATED ON ACCEPTANCE, and that gate is load-bearing. Adoption only ever MARKS a number in use and
        // never frees one, which is why the old roster-read placement was safe - it read the AUTHORITATIVE
        // assembled roster. The ingress is different: a push from a superseded connection or a stale
        // sequence is NOT authoritative, and adopting from one would reserve a number that no live session
        // holds, permanently, because nothing here can ever give it back.
        if (accepted)
            AdoptNumbers(directorId, set);
        // A push the store REJECTED (from a superseded connection, or a stale sequence) is NOT authoritative,
        // so it must not drive the snooze observer - whose edges MUTATE the authoritative registry
        // (ClearIfArmed deletes an armed snooze, Land converts a deferral). A rejected stale Working push
        // could otherwise delete a snooze the current connection owns, and a rejected settled push could land
        // a deferral while the authoritative session is still working. The roles/display observers below read
        // the STORE - unchanged by a rejected push - so they are self-correcting and need no gate.
        // Defect 20: a deferred snooze whose hold landed while this Director was disconnected arrives in
        // the reconnect snapshot, not as a delta - so the snapshot must be watched too, or the landing is
        // missed until the sweep's backstop notices.
        if (accepted)
            _snoozeLandings?.ObserveSnapshot(set);
        // Defect 5: a reconnecting Director's whole roster can change roles across the FLEET (its sessions
        // re-enter the liveness set, so their controllers' and workers' roles move with them). Re-resolve
        // and stamp down whatever changed, or the desktop keeps folding a role from before the reconnect.
        _fleetRoles?.ObserveSnapshot(set);
        // The fold seam: a reconnecting Director's whole roster can change any session's folded display state
        // (its own, and others' across the fleet). Re-fold and stamp down whatever changed, so the desktop
        // rail renders the Gateway's answer rather than one from before the reconnect.
        _fleetDisplayState?.ObserveSnapshot(set);
        // Issue #2194: fold the accepted roster into durable work history. Gated on acceptance like the
        // snooze observer - a rejected stale snapshot must not close rows the current connection owns.
        // The snapshot is authoritative, so this also reconciles sessions removed while the tunnel was
        // down (the Director's per-session remove no-ops when disconnected).
        if (accepted)
            _sessionHistory?.ObserveSnapshot(RequireBoundTenant(), directorId, set);
    }

    /// <summary>A single-session delta: upserts one session for the bound Director.</summary>
    public void PushDelta(long sequence, SessionDto session)
    {
        var directorId = RequireBoundDirector();
        if (session is null || string.IsNullOrEmpty(session.SessionId))
        {
            FileLog.Write($"[DirectorHub] PushDelta ignored (no session id): director={directorId}, conn={Short(Context.ConnectionId)}");
            return;
        }
        // Hosted Multi-Tenancy increment 1: run the handler in the bound tenant's scope (the landing seam
        // below writes the snooze/spend EF stores, which must stamp and filter by this connection's tenant).
        using var tenantScope = EnterBoundTenantScope();
        var accepted = _store.ApplyDelta(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, session);
        // DevThrottle Stats: fold this session's tally into the always-available aggregate, under this
        // connection's bound tenant (MTR-08). Offered to the queue, never written here - see PushSnapshot.
        var deltaTenant = RequireBoundTenant();
        _statsQueue?.Offer(Stats.StatisticsObservationQueue.InputStatsObserver,
            _ => { _inputStats.Observe(session, tenant: deltaTenant); return Task.CompletedTask; });
        // Concurrency is timer-sampled, not observed here - and this is the HOTTEST path in the Gateway, so
        // it is the one that must never materialise the whole tenant fleet or touch the shared file.
        // Issue #1292: claim this session's number the moment it arrives - gated on acceptance so a stale or
        // superseded push cannot reserve a phantom number (see the snapshot path).
        if (accepted)
            AdoptNumbers(directorId, new[] { session });
        // A push the store REJECTED (superseded connection, or a stale sequence) is NOT authoritative, so it
        // must not drive the snooze observer, whose edges MUTATE the authoritative registry (ClearIfArmed
        // deletes an armed snooze, Land converts a deferral). See the note in PushSnapshot. The roles/display
        // observers read the store and are self-correcting.
        // Defect 20: THE landing seam. A settled activity from the Director arrives here within milliseconds
        // of the turn ending, which is the exact moment a deferred snooze's clock must start.
        if (accepted)
            _snoozeLandings?.Observe(session);
        // Defect 5: THE ROLE SEAM. This session's own facts may have changed its role (it gained a
        // controller), and its arrival may change ANOTHER session's role on ANOTHER Director (this session
        // just exited, so the worker it controlled is no longer a Worker and its red must surface). Either
        // way the resolution is fleet-wide, and the changed roles are stamped back down so every desktop
        // folds the same answer the phone does.
        _fleetRoles?.Observe(session);
        // THE FOLD SEAM. This delta changed this session's raw facts (activity, hold, dictation) and may
        // change another session's fold across the fleet. Re-fold and stamp the changed answers down, so the
        // desktop rail shows the Gateway's colour/label/triage within milliseconds of the change.
        _fleetDisplayState?.Observe(session);
        // Issue #2194: durable work history rides the same accepted delta. Throttled inside; a
        // rejected push from a superseded connection must not touch the record.
        if (accepted)
            _sessionHistory?.Observe(RequireBoundTenant(), directorId, session);
    }

    /// <summary>A remove/tombstone: drops one session from the bound Director's set.</summary>
    public void RemoveSession(long sequence, string sessionId)
    {
        var directorId = RequireBoundDirector();
        if (string.IsNullOrEmpty(sessionId))
        {
            FileLog.Write($"[DirectorHub] RemoveSession ignored (no session id): director={directorId}, conn={Short(Context.ConnectionId)}");
            return;
        }
        // Hosted Multi-Tenancy increment 1: scope the handler to the bound tenant (the removal re-folds and
        // can touch tenant-scoped state through the observers below).
        using var tenantScope = EnterBoundTenantScope();
        var accepted = _store.ApplyRemove(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, sessionId);
        // DevThrottle Stats: its contribution stays in the totals; drop only its high-water entry, scoped to
        // this connection's bound tenant (MTR-08) so it cannot drop another tenant's same-id high-water.
        // Contained like every other statistics call here: this one was missed in the first pass, and an
        // unwrapped throw would have failed the whole removal - skipping the role, display and history
        // observers below - for a high-water row. That is the same shape as the outage, one method over.
        var removeTenant = RequireBoundTenant();
        _statsQueue?.Offer(Stats.StatisticsObservationQueue.InputStatsObserver,
            _ => { _inputStats.Forget(sessionId, removeTenant); return Task.CompletedTask; });
        // A departure changes how many sessions are live, but concurrency is timer-sampled, so the next
        // sample picks it up rather than this path writing to the shared file.
        // A DEPARTURE RE-ROLES THE SURVIVORS, exactly as an arrival does: a controller leaving should stop
        // its workers being Workers. Must run AFTER ApplyRemove so the sweep resolves the fleet that now
        // exists rather than the one that just left.
        _fleetRoles?.ObserveRemoval(sessionId);
        // A departure re-folds the survivors too (a controller leaving un-suppresses its workers' red).
        _fleetDisplayState?.ObserveRemoval(sessionId);
        // Issue #2194: a per-session remove from the CURRENT connection is the session's farewell -
        // the recorder rules "finished" or "closed" from the last pushed facts and stamps the row.
        // Gated on acceptance, unlike the self-correcting observers above: an ending stamped from a
        // superseded connection's stale remove would stick, because only "interrupted" reopens.
        if (accepted)
            _sessionHistory?.ObserveRemoval(RequireBoundTenant(), directorId, sessionId);
    }

    /// <summary>
    /// The Director's clean-shutdown farewell (issue #2194, the #1862 ending design). Sent by a
    /// stopping Director just before it closes the tunnel, AFTER its per-session removes have flowed:
    /// every still-open work-history row of this Director is ruled "director-stopped". A Director that
    /// never says goodbye is caught by the history sweep's silence rule instead ("interrupted") - the
    /// two rulings are exactly what tells a clean stop from a power cut. Older Directors simply never
    /// call this; nothing here is required for the stream to function.
    /// </summary>
    public void DirectorStopping()
    {
        var directorId = RequireBoundDirector();
        using var tenantScope = EnterBoundTenantScope();
        FileLog.Write($"[DirectorHub] DirectorStopping: director={directorId} conn={Short(Context.ConnectionId)}");
        _sessionHistory?.ObserveDirectorStopping(RequireBoundTenant(), directorId);
    }

    public override Task OnConnectedAsync()
    {
        FileLog.Write($"[DirectorHub] connected: conn={Short(Context.ConnectionId)}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var directorId = BoundDirectorId();
        var tenant = BoundTenant();
        if (directorId is not null && tenant is { } t)
        {
            // Clear the active connection so aggregation falls back to the cached roster. Gateway Cleanup
            // mission (tunnel-only): do NOT drop the registry entry here - a dead Director's cached roster must
            // survive the sweep window (so a Gateway-owned snooze still fires it back to "needs you" from the
            // cache) and a brief reconnect blip must not flap the roster. The stale sweeper ages out a Director
            // that stops refreshing LastSeen (HttpHeartbeatTimeout); a reconnect re-Hellos and refreshes it.
            // The tenant is the one bound at Hello (Hello sets both, so a bound director always has a tenant).
            _store.UnregisterConnection(t, directorId, Context.ConnectionId);
            _repositoryStore?.UnregisterConnection(t, directorId, Context.ConnectionId);
        }
        // MTR-15: drop this connection's abort entry (it is gone now). Keyed by connection id, so it is cleared
        // whether or not a tenant was bound.
        _connections?.Unregister(Context.ConnectionId);
        FileLog.Write($"[DirectorHub] disconnected: conn={Short(Context.ConnectionId)}, director={directorId ?? "(unbound)"} ({exception?.Message ?? "clean"})");
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Resolve this connection's tenant from the AUTHENTICATED device key the auth layer stashed on the
    /// negotiate request, via the hosted tenant boundary - NEVER from the Hello payload. Self-host (or no
    /// boundary) resolves to Local, unchanged. Hosted resolves to the key's bound tenant, or null (a DENY)
    /// when the key has no binding. This is the isolation resolution the whole design rests on.
    /// </summary>
    private TenantId? ResolveConnectionTenant()
    {
        if (_tenantBoundary is null)
            return TenantId.Local;

        var httpContext = Context.GetHttpContext();
        return httpContext is null ? null : _tenantBoundary.ResolveRequestTenant(httpContext);
    }

    /// <summary>Enter the bound tenant's scope for the duration of a push handler, so the EF-writing observers
    /// stamp the right tenant. A no-op on self-host (Local is ambient) or when there is no boundary.</summary>
    private IDisposable EnterBoundTenantScope() =>
        _tenantBoundary is null ? NoScope.Instance : _tenantBoundary.EnterScope(RequireBoundTenant());

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private string? BoundDirectorId() =>
        Context.Items.TryGetValue(DirectorIdItemKey, out var value) && value is string id ? id : null;

    private string RequireBoundDirector() =>
        BoundDirectorId() ?? throw new HubException("Director stream not initialized: send Hello first.");

    private TenantId? BoundTenant() =>
        Context.Items.TryGetValue(TenantIdItemKey, out var value) && value is TenantId t ? t : null;

    private TenantId RequireBoundTenant() =>
        BoundTenant() ?? throw new HubException("Director stream not initialized: send Hello first.");

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
