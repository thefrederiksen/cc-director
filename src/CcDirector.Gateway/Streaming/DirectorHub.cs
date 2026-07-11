using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
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

    private readonly PushedSessionStore _store;
    private readonly DirectorRegistry _registry;
    private readonly GatewayInputStatsAggregator _inputStats;

    public DirectorHub(PushedSessionStore store, DirectorRegistry registry, GatewayInputStatsAggregator inputStats)
    {
        _store = store;
        _registry = registry;
        _inputStats = inputStats;
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

        Context.Items[DirectorIdItemKey] = directorId;
        _store.RegisterConnection(directorId, Context.ConnectionId);
        _registry.MarkStateReporting(directorId);
        FileLog.Write($"[DirectorHub] Hello: director={directorId} bound to conn={Short(Context.ConnectionId)} (version={hello.Version})");
    }

    /// <summary>A full snapshot: replaces the bound Director's session set (pruning anything absent).</summary>
    public void PushSnapshot(long sequence, SessionDto[] sessions)
    {
        var directorId = RequireBoundDirector();
        var set = sessions ?? Array.Empty<SessionDto>();
        _store.ApplySnapshot(directorId, Context.ConnectionId, sequence, set);
        // DevThrottle Stats: fold each session's input tally into the always-available aggregate.
        _inputStats.ObserveSnapshot(set);
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
        _store.ApplyDelta(directorId, Context.ConnectionId, sequence, session);
        // DevThrottle Stats: fold this session's tally into the always-available aggregate.
        _inputStats.Observe(session);
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
        _store.ApplyRemove(directorId, Context.ConnectionId, sequence, sessionId);
        // DevThrottle Stats: its contribution stays in the totals; drop only its high-water entry.
        _inputStats.Forget(sessionId);
    }

    public override Task OnConnectedAsync()
    {
        FileLog.Write($"[DirectorHub] connected: conn={Short(Context.ConnectionId)}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var directorId = BoundDirectorId();
        if (directorId is not null)
            _store.UnregisterConnection(directorId, Context.ConnectionId);
        FileLog.Write($"[DirectorHub] disconnected: conn={Short(Context.ConnectionId)}, director={directorId ?? "(unbound)"} ({exception?.Message ?? "clean"})");
        return base.OnDisconnectedAsync(exception);
    }

    private string? BoundDirectorId() =>
        Context.Items.TryGetValue(DirectorIdItemKey, out var value) && value is string id ? id : null;

    private string RequireBoundDirector() =>
        BoundDirectorId() ?? throw new HubException("Director stream not initialized: send Hello first.");

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
