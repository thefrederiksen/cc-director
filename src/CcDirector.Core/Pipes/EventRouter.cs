using CcDirector.Core.Sessions;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Routes pipe messages to the correct Session by session_id.
/// Session ID discovery is handled by terminal content matching;
/// this router only delivers events to already-linked sessions.
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
            _log?.Invoke($"No linked session for Claude session {msg.SessionId[..Math.Min(8, msg.SessionId.Length)]}... (event={msg.HookEventName}), skipping.");
            return;
        }

        _log?.Invoke($"Routing {msg.HookEventName} to session {session.Id} (claude={msg.SessionId[..Math.Min(8, msg.SessionId.Length)]}...)");
        session.HandlePipeEvent(msg);
    }
}
