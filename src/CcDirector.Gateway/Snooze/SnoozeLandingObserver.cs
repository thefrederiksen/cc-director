using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// Starts a deferred snooze's clock the moment the hold LANDS (defect 20). This is the PUSH seam: every
/// session state the Director pushes up its tunnel passes through here, and a hold that has landed
/// (<see cref="HoldStates.Held"/>) converts its deferred registry entry into an armed one.
///
/// WHY THIS EXISTS. The owner's ruling (14 July 2026) is that the snooze clock starts when the work ENDS:
/// "snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes. So the
/// Gateway cannot arm the clock when the snooze is REQUESTED - at that moment the agent is still working
/// and the hold has not landed - and something has to notice the landing. The Director already tells us:
/// <c>Session.HoldStateChanged</c> fires on EVERY hold transition including DeferredHold -&gt; Held, and
/// the Control API already pushes a delta on that event. So the landing arrives on its own, promptly,
/// with no polling - this observer just has to read it.
///
/// The expiry sweep is the BACKSTOP for the same landing (it re-reads each Director every 15 seconds), so
/// a missed or dropped push costs at most one sweep interval rather than the whole snooze.
/// <see cref="SnoozeRegistry.Land"/> is idempotent, so whichever of the two gets there first wins and the
/// other changes nothing - in particular a landing never restarts a clock that is already running.
///
/// Cost: one dictionary lookup per pushed session, and only for a session whose hold reads Held. A
/// session with no registry entry - the overwhelming majority - is rejected by that lookup.
/// </summary>
public sealed class SnoozeLandingObserver
{
    private readonly SnoozeRegistry _registry;
    private readonly Func<DateTime> _utcNow;

    /// <param name="registry">The registry holding the deferred entry to land.</param>
    /// <param name="utcNow">The clock; injected so the landing instant is deterministic in tests.</param>
    public SnoozeLandingObserver(SnoozeRegistry registry, Func<DateTime>? utcNow = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Observe one pushed session. Lands its deferred snooze if - and only if - the Director now reports
    /// the hold as <see cref="HoldStates.Held"/>. Anything else is ignored: a <c>DeferredHold</c> has not
    /// landed yet (it is still waiting for the work to stop) and a <c>None</c> is handled by the sweep,
    /// which is the one place a snooze is ever cleared.
    /// </summary>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId))
            return;
        if (!HoldStates.IsHeld(session.HoldState))
            return;
        if (_registry.Land(session.SessionId, _utcNow()))
            FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: deferred hold landed on a push -> clock started");
    }

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a landing can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null) return;
        foreach (var s in sessions)
            Observe(s);
    }
}
