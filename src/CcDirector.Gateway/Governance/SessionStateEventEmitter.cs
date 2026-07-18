using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// Fills the append-only governance event ledger (issue #1771, spine item 2) with session state transitions,
/// so a duration report can measure active / idle / waiting time. It observes the Gateway's single
/// session-state funnel (which fires on every doorbell ping and heartbeat) and appends a ledger event ONLY on
/// a real transition - a repeat of the current state is skipped, so a heartbeat storm never floods the ledger.
///
/// The six ledger states are entered; the reader closes each interval at the NEXT event of any state, so a
/// normal came-back needs no synthetic event. The one exception, for honesty: a session that EXITS while in an
/// open wait/block interval would otherwise have its wait counted to the report window's end, so on exit from
/// waiting-on-human / waiting-on-permission / blocked this emits one closing event at the true exit time.
/// <see cref="ActivityState"/>'s Starting and Exited are lifecycle, not activity states, so they are not
/// emitted as-is (and the ledger would reject them anyway).
/// </summary>
public sealed class SessionStateEventEmitter
{
    private readonly GovernanceEventLedger _ledger;

    // The last ledger state emitted per session, so an event lands only on a real change. Cleared on exit so
    // the map does not grow without bound. Per-session observations are ordered by the funnel, so the
    // read-then-write is effectively serial per session; a rare race would at worst emit a duplicate the
    // reader tolerates.
    private readonly ConcurrentDictionary<string, string> _lastState = new(StringComparer.Ordinal);

    public SessionStateEventEmitter(GovernanceEventLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>
    /// Observe a session's reported activity state. Appends a ledger event on a real transition; on exit,
    /// closes an open wait/block interval at the true exit time and forgets the session.
    /// </summary>
    public void Observe(string sessionId, string? activityState)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        var mapped = MapState(activityState);
        if (mapped is null)
        {
            // Not one of the six ledger states. On exit, close any open wait/block interval at the true exit
            // (so a mid-wait exit is not overcounted to the window end), then forget the session.
            if (IsExit(activityState) &&
                _lastState.TryRemove(sessionId, out var lastOnExit) &&
                IsOpenInterval(lastOnExit))
            {
                Append(sessionId, GovernanceEventState.Recovered);
            }
            return;
        }

        // A real transition only - a heartbeat re-reporting the current state must not append a duplicate.
        var previous = _lastState.GetValueOrDefault(sessionId);
        if (string.Equals(previous, mapped, StringComparison.Ordinal))
            return;

        _lastState[sessionId] = mapped;
        Append(sessionId, mapped);
    }

    private void Append(string sessionId, string state)
    {
        _ledger.Append(new AppendGovernanceEventRequest
        {
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = state,
            OccurredUtc = null, // the Gateway stamps the append time
        });
        FileLog.Write($"[SessionStateEventEmitter] Append: session={sessionId}, state={state}");
    }

    /// <summary>
    /// Map a Director <see cref="ActivityState"/> name to a ledger state, or null when it is not one of the
    /// six (Starting and Exited are lifecycle, not activity states). Case-insensitive.
    /// </summary>
    public static string? MapState(string? activityState) => Normalize(activityState) switch
    {
        "working" => GovernanceEventState.Active,
        "idle" => GovernanceEventState.Idle,
        "waitingforinput" => GovernanceEventState.WaitingOnHuman,
        "waitingforperm" => GovernanceEventState.WaitingOnPermission,
        _ => null,
    };

    private static bool IsExit(string? activityState) =>
        string.Equals(Normalize(activityState), "exited", StringComparison.Ordinal);

    /// <summary>An open wait/block interval whose duration a report measures - it must be closed on exit so it
    /// is not counted to the report window's end.</summary>
    private static bool IsOpenInterval(string state) =>
        state == GovernanceEventState.WaitingOnHuman ||
        state == GovernanceEventState.WaitingOnPermission ||
        state == GovernanceEventState.Blocked;

    private static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant();
}
