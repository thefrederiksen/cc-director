namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The fleet operations the Car Mode brain's tools call (Car Mode mission, New build A). Each method is
/// backed by an existing Gateway or Director endpoint - no new fleet mechanics, just a wrapper the model
/// can call in the tool-calling loop. Phase 2 is read-only (list + activity); Phase 3 adds the act tools
/// (start / message / approve) and the confirmed-destructive tools (delete).
///
/// This is an interface so the brain's tool loop is unit-tested against a fake fleet (no network), and
/// the production implementation talks to the Gateway's own endpoints.
/// </summary>
public interface ICarModeFleet
{
    /// <summary>The whole fleet roster as compact, speakable session views. Answers count / list /
    ///  "who needs me" / "the latest one". Ordered newest-created first so "the latest one" is index 0.</summary>
    Task<IReadOnlyList<CarModeSessionInfo>> ListSessionsAsync(CancellationToken ct);

    /// <summary>Resolve a fuzzy human reference (a name, a repo, or a number) to one session and return
    ///  what it is doing. Returns null when nothing matches, so the brain can say it plainly rather than
    ///  guess.</summary>
    Task<CarModeActivity?> GetSessionActivityAsync(string sessionReference, CancellationToken ct);

    /// <summary>Resolve a fuzzy human reference to one session's identity (id + name + repo), or null when
    ///  nothing matches. Used by the act tools to name what they are about to do and to address it by id.</summary>
    Task<CarModeSessionInfo?> ResolveSessionAsync(string sessionReference, CancellationToken ct);

    /// <summary>Start a new session in a repository named by the owner (its leaf name). Resolves the repo
    ///  to a real path on a machine and creates the session the same way the "+ New session" button does.
    ///  Returns a short spoken-ready summary of what was started. Throws with a clear reason when no
    ///  repository of that name exists on any machine (no silent guess).</summary>
    Task<string> StartSessionAsync(string repo, CancellationToken ct);

    /// <summary>Send a message (a prompt) into a session, exactly the way the Send button does
    ///  (POST /prompt, appendEnter). Addressed by session id (already resolved).</summary>
    Task MessageSessionAsync(string sessionId, string message, CancellationToken ct);

    /// <summary>Approve a waiting session by pressing Enter to accept its highlighted default, exactly the
    ///  way the Enter button does (POST /prompt "\r", appendEnter false). Addressed by session id.</summary>
    Task ApproveSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>Delete (kill) a session, exactly the way the remove button does (DELETE /sessions/{id}).
    ///  Destructive: the brain only calls this AFTER a spoken confirmation. Addressed by session id.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct);
}
