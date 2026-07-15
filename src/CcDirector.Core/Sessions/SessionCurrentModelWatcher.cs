using System.Collections.Concurrent;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Keeps <see cref="Session.CurrentModel"/> fresh (issue #1637): every time a session's turn ends
/// (the detector flips it to <see cref="ActivityState.WaitingForInput"/> - the same trigger
/// <see cref="Storage.TurnReviewLogger"/> uses), it asks the session's driver what model the agent
/// is CURRENTLY using (<see cref="IAgentDriver.ReadCurrentModel"/>, capability
/// <see cref="DriverCapabilities.ModelReport"/>) and stamps the answer on the session. The stamped
/// value rides the existing snapshot/delta path to the Gateway on
/// <see cref="Gateway.Contracts.SessionDto.CurrentModel"/>, where the statistics fold consumes it.
///
/// Turn-end-driven ON PURPOSE, not read in the DTO mapper: ReadCurrentModel reads the agent's
/// on-disk records (transcript walk, rollout scan, SQLite query), and the mapper runs on every
/// roster snapshot - exactly the per-poll O(history) work the Gateway statistics mission calls out
/// as the anti-pattern. A turn-end read runs once per completed turn, off the detector's callback
/// thread. It also stamps once at wire-up, so a session launched with an explicit model flag (the
/// pre-first-turn answer for Claude) reports it before any turn completes.
///
/// A session whose driver does not declare ModelReport is never asked (no NotSupportedException as
/// control flow); its CurrentModel honestly stays null. One instance per Director.
/// </summary>
public sealed class SessionCurrentModelWatcher : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private bool _started;
    private int _disposed;

    public SessionCurrentModelWatcher(SessionManager sessionManager) => _sessionManager = sessionManager;

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[SessionCurrentModelWatcher] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        if (!session.Driver.Capabilities.HasFlag(DriverCapabilities.ModelReport))
            return;
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState; // unused: we only care about the state we transitioned INTO
            // The single trigger: the turn just ended. The agent's records now carry the model that
            // produced it.
            if (newState != ActivityState.WaitingForInput) return;
            Task.Run(() => RefreshModel(session));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // Initial stamp: pre-first-turn a driver may already know the model (Claude answers from the
        // --model launch value; repo-located agents may find a prior session's records).
        _ = Task.Run(() => RefreshModel(session));
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
    }

    /// <summary>
    /// Ask the driver for the current model and stamp it. A boundary (fire-and-forget target): it
    /// owns its try/catch so a records-read fault never escapes onto a background thread. A null
    /// answer is a missed read and stamps nothing (<see cref="Session.SetCurrentModel"/> ignores it).
    /// </summary>
    internal static void RefreshModel(Session session)
    {
        try
        {
            // EffectiveLaunchArgs (the merged launch line) carries the launched --model even when it
            // came from the configured default; ClaudeArgs alone is null in that case (#803).
            var launchArgs = session.EffectiveLaunchArgs ?? session.ClaudeArgs;
            var model = session.Driver.ReadCurrentModel(session.ClaudeSessionId ?? "", session.RepoPath, launchArgs);
            session.SetCurrentModel(model);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionCurrentModelWatcher] refresh FAILED: session={session.Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
    }
}
