using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Routes pipe messages to the correct Session by session_id.
/// Auto-registers unknown session_ids by matching to unmatched sessions.
/// Validates that session IDs belong to the correct repo to prevent orphan process mixups.
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

            // CRITICAL: Validate that the Claude session ID actually belongs to this repo.
            // This prevents orphaned claude.exe processes from hijacking sessions with wrong IDs.
            // Orphans may send hook events with their old session IDs, causing mixups.
            var verification = ClaudeSessionReader.VerifySessionFile(msg.SessionId, unmatched.RepoPath);
            if (verification.Status != SessionVerificationStatus.Verified)
            {
                _log?.Invoke($"Rejecting auto-registration of {msg.SessionId[..8]}... to {unmatched.RepoPath}: " +
                             $"session file not found (status={verification.Status}). " +
                             $"This may be an orphaned claude.exe from a different repo.");
                return;
            }

            _sessionManager.RegisterClaudeSession(msg.SessionId, unmatched.Id);
            session = unmatched;
            _log?.Invoke($"Auto-registered Claude session {msg.SessionId} -> Director session {session.Id}.");
        }

        session.HandlePipeEvent(msg);
    }
}
