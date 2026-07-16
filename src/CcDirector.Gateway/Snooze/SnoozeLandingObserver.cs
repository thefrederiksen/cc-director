using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// THE PUSH SEAM, and where the Gateway drives the hold machine. Every session state a Director pushes up
/// its tunnel passes through here, carrying the only two facts a Director contributes: what it is doing
/// (<c>ActivityState</c>) and whether the owner just drove a turn (<c>LastOwnerTurnAtUtc</c>). This class
/// turns those observations into the Gateway's rulings.
///
/// It used to read the Director's own <c>HoldState</c> and believe it - which meant the ruling was made on
/// the Director and merely reported here. That is the architecture this replaces. A Director does not
/// decide hold: a hold is the owner's intent, and a Director's world is bytes and processes.
///
/// THE RULING (owner, 14 July 2026) that the landing edge implements: the snooze clock starts when the
/// work ENDS. "Snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes,
/// so the clock cannot be armed when the snooze is asked for - the agent is still working then.
/// <see cref="SnoozeRegistry.Land"/> is idempotent, so a settled session re-pushing its state repeatedly
/// never restarts a running clock.
///
/// Cost: one lookup per pushed session, and the overwhelming majority - anything with no hold - is
/// rejected by it immediately.
/// </summary>
public sealed class SnoozeLandingObserver
{
    private readonly SnoozeRegistry _registry;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, string, string, Task>? _pushMirror;

    /// <param name="registry">The registry holding the deferred entry to land.</param>
    /// <param name="utcNow">The clock; injected so the landing instant is deterministic in tests.</param>
    /// <param name="pushMirror">
    /// Stamps this Gateway's ruling back DOWN to the owning Director's display mirror
    /// (directorId, sessionId, holdState), so the local desktop rail renders what the Gateway decided.
    /// Same shape and purpose as FleetRoleObserver's role stamp-down. Null in tests that assert only on
    /// the registry - the truth - because the mirror is display, not state.
    ///
    /// This exists because the Gateway moves the hold on its OWN initiative here (a landing, an exit, an
    /// owner-turn clear). The hold ENDPOINT can push its mirror inline because a caller is waiting; these
    /// transitions have no caller, so without this the desktop would keep rendering the state the last
    /// hold call left behind while the phone showed the truth. Two surfaces disagreeing about hold is the
    /// entire disease being cured, and it is not cured by moving where it happens.
    /// </param>
    public SnoozeLandingObserver(
        SnoozeRegistry registry,
        Func<DateTime>? utcNow = null,
        Func<string, string, string, Task>? pushMirror = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pushMirror = pushMirror;
    }

    /// <summary>Fire-and-forget the mirror down to the Director. Best-effort by design: the hold is
    /// already recorded, and a Director that misses this is wrong only on its own rail, only until the
    /// next push.</summary>
    private void Mirror(string directorId, string sessionId, string holdState)
    {
        if (_pushMirror is null || string.IsNullOrEmpty(directorId)) return;
        _ = Task.Run(async () =>
        {
            try { await _pushMirror(directorId, sessionId, holdState); }
            catch (Exception ex) { FileLog.Write($"[SnoozeLandingObserver] mirror push failed sid={sessionId}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Observe one pushed session and drive the hold machine from it.
    /// </summary>
    ///
    /// <remarks>
    /// This reads the session's ACTIVITY, never its hold. That is the whole architecture in one method: a
    /// Director reports the one fact only it can see - am I working - and the GATEWAY decides what that
    /// fact means for the owner's hold. It used to read <c>session.HoldState</c>, which meant asking a
    /// Director to decide, and then believing it.
    ///
    /// Two edges, both of them the Gateway's ruling, not the Director's:
    ///  * SETTLED (anything that is not Working, Starting or Exited) - the work the deferral was waiting
    ///    for has ended, so the deferral lands and the clock starts. The owner's ruling (14 July 2026):
    ///    "snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes.
    ///  * EXITED - drop the hold entirely. There is no turn to come back to, and a dead session must never
    ///    hide behind a "Snoozed" label.
    ///
    /// Deliberately NOT an edge: Working. A held session that starts working STAYS HELD. Activity is not
    /// consent - another agent's fleet message is real work, and a bare terminal repaint reads as work
    /// too. Only the owner lifts a hold, and the owner never speaks through this seam.
    /// </remarks>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId))
            return;
        // One lookup, under one lock: nothing held -> nothing to decide, which is the overwhelming
        // majority of pushes. A Contains() followed by a separate scan of Entries() would read the map
        // twice with a gap the entry could vanish through.
        var directorId = _registry.DirectorIdFor(session.SessionId);
        if (directorId is null)
            return;

        // The owner came back and drove a turn: the hold is over, whatever the session is doing. Checked
        // FIRST, because it beats every other edge - a hold exists to stop bothering someone who is away,
        // and they are demonstrably not away.

        if (_registry.ClearIfSupersededByOwnerTurn(session.SessionId, session.LastOwnerTurnAtUtc))
        {
            Mirror(directorId, session.SessionId, HoldStates.None);
            return;
        }

        var activity = (session.ActivityState ?? "").Trim();

        if (string.Equals(activity, nameof(ActivityState.Exited), StringComparison.OrdinalIgnoreCase))
        {
            if (_registry.Clear(session.SessionId))
            {
                FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: session exited -> hold dropped");
                Mirror(directorId, session.SessionId, HoldStates.None);
            }
            return;
        }

        if (IsSettled(activity) && _registry.Land(session.SessionId, _utcNow()))
        {
            FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: work ended -> deferred hold landed, clock started");
            Mirror(directorId, session.SessionId, HoldStates.Held);
        }
    }

    /// <summary>
    /// Has the work ENDED? Settled is everything that is not actively running and not dead: the agent is
    /// sitting at its prompt, or waiting on a permission answer, or idle. Starting is excluded - a session
    /// still coming up has not finished anything yet - and Working is obviously excluded. An unrecognised
    /// value is NOT settled: never land a deferral on a state we do not understand, because landing starts
    /// a clock and a clock started too early expires too early.
    /// </summary>
    private static bool IsSettled(string activity) =>
        string.Equals(activity, nameof(ActivityState.WaitingForInput), StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, nameof(ActivityState.WaitingForPerm), StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, nameof(ActivityState.Idle), StringComparison.OrdinalIgnoreCase);

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a landing can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null) return;
        foreach (var s in sessions)
            Observe(s);
    }
}
