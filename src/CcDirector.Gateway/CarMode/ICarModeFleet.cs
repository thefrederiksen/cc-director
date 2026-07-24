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

    /// <summary>Read a session's current spoken narration, exactly the way the Voice screen's onSwitchOn does
    ///  (POST /sessions/{id}/wingman/explain): the Gateway reads the session's latest completed turn and
    ///  returns the speakable summary. The brain reads this REAL narration aloud - never a canned line.
    ///  Addressed by session id (already resolved).</summary>
    Task<CarModeExplain> ExplainSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>Switch a session into (or out of) voice mode, exactly the way the Voice screen's Switch button
    ///  does (POST /sessions/{id}/voice-mode { enabled }). Addressed by session id (already resolved).</summary>
    Task SwitchVoiceModeAsync(string sessionId, bool enabled, CancellationToken ct);

    /// <summary>Snooze a session, exactly the way the per-session Snooze button does
    ///  (POST /sessions/{id}/hold { onHold: true }). Snooze IS the hold plus a Gateway-owned timer: the
    ///  Gateway records a snooze-until from the per-user default length, so the session RETURNS to "needs
    ///  you" on its own when the timer fires (Snooze Length mission). Addressed by session id.</summary>
    Task SnoozeSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>The account credit balance, exactly what the Settings account section shows
    ///  (GET /account/credits). SignedIn false means there is no account credential and no balance -
    ///  the brain says so plainly rather than inventing a number.</summary>
    Task<CarModeCredits> GetCreditsAsync(CancellationToken ct);

    /// <summary>The machines running Directors right now (GET /directors grouped by machine), each with how
    ///  recently the Gateway heard from it and how many roster sessions it carries. Answers "which machines
    ///  are online" without touching the hosted-denied /machines and /launchers surfaces.</summary>
    Task<IReadOnlyList<CarModeMachineInfo>> ListMachinesAsync(CancellationToken ct);

    /// <summary>The scheduled jobs (GET /cron/jobs) as compact speakable views: name, on/off, when, where,
    ///  and what each fire does, plus the next due time and the last outcome.</summary>
    Task<IReadOnlyList<CarModeScheduleInfo>> ListSchedulesAsync(CancellationToken ct);

    /// <summary>The hosted AI spend total over the trailing <paramref name="days"/> days
    ///  (GET /gateway/governance/hosted-ai-spend/summary). Real ledger dollars, never an estimate.</summary>
    Task<CarModeSpendSummary> GetSpendAsync(int days, CancellationToken ct);
}
