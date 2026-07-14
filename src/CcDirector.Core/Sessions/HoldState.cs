namespace CcDirector.Core.Sessions;

/// <summary>
/// Where a session sits in the hold ("Snooze") state machine - the user's "I do not want to deal with
/// this one right now". Design and diagram: docs/new_architecture/session-state.html.
///
/// The whole machine, driven by two things: what the user pressed, and whether the agent is working
/// (<see cref="ActivityState"/>, the authoritative live fact - never a latch):
///
/// <code>
///                      +---------------- it starts working ----------------+
///                      v                                                   |
///   [None] --- Snooze pressed while settled -------------------------> [Held]
///     ^                                                                    ^
///     |--- Unsnooze pressed ---------------------------------------------- |
///     |                                                                    |
///     |--- you send a prompt (supersedes) ---+                             |
///     |                                      |                             |
///     +--- Snooze pressed while working -> [DeferredHold] --- it stops ----+
/// </code>
///
/// The load-bearing edge is Held + it starts working -> None: a held session that comes back to life
/// takes itself off hold, every time, and its snooze timer is cancelled with it. A session can never be
/// simultaneously held and working - that combination is unreachable by construction, which is the point.
/// </summary>
public enum HoldState
{
    /// <summary>Not held. The session's own activity state is the whole story.</summary>
    None = 0,

    /// <summary>
    /// Parked by the user: shown as "Snoozed", sunk to the bottom of the roster, skipped by the FIFO
    /// conductor, and never raised as "needs you". Left the instant the agent starts working again.
    /// </summary>
    Held = 1,

    /// <summary>
    /// The user pressed Snooze while the agent was working: "hold this one when it finishes". NOT parked
    /// yet - the session is still working and still reads as working on every screen - but it parks
    /// (-&gt; <see cref="Held"/>) the moment the work stops. Superseded by a fresh prompt, and dropped if
    /// the session exits (there is no turn left to come back to).
    /// </summary>
    DeferredHold = 2,
}
