using CcDirector.Core.Sessions;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Routes pipe messages to the correct Session by session_id.
/// Auto-registers unknown session_ids by matching to unmatched sessions.
/// </summary>
public sealed class EventRouter
{
    private readonly SessionManager _sessionManager;
    private readonly Action<string>? _log;

    /// <summary>Raised for every message regardless of routing, for UI display.</summary>
    public event Action<PipeMessage>? OnRawMessage;

    public EventRouter(SessionManager sessionManager, Action<string>? log = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _log = log;
    }

    /// <summary>Route a pipe message to its target session.</summary>
    public void Route(PipeMessage msg)
    {
        OnRawMessage?.Invoke(msg);

        if (string.IsNullOrEmpty(msg.SessionId))
        {
            _log?.Invoke($"Received {msg.HookEventName} with no session_id, skipping.");
            return;
        }

        var session = _sessionManager.GetSessionByClaudeId(msg.SessionId);

        if (session == null)
        {
            // Try to auto-register by matching unmatched sessions
            var unmatched = _sessionManager.FindUnmatchedSession(msg.Cwd);
            if (unmatched == null)
            {
                _log?.Invoke($"No unmatched session for Claude session {msg.SessionId} (cwd: {msg.Cwd}), skipping.");
                return;
            }

            _sessionManager.RegisterClaudeSession(msg.SessionId, unmatched.Id);
            session = unmatched;
            _log?.Invoke($"Auto-registered Claude session {msg.SessionId} → Director session {session.Id}.");
        }

        session.HandlePipeEvent(msg);
    }
}
