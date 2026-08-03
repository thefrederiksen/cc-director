namespace CcDirector.Gateway.Snooze;

/// <summary>
/// ONE fold's worth of hold rows, read from the snooze store in a single set-based query, and the three
/// answers the fold needs from them. This is the read side of issue #2323 and of the load-test finding it
/// closes: the fold used to ask the registry three separate per-session questions
/// (<see cref="SnoozeRegistry.HoldStateFor"/>, <see cref="SnoozeRegistry.IsExpired"/>,
/// <see cref="SnoozeRegistry.SnoozeUntilFor"/>), and every one of them took the registry's process-wide
/// monitor, rented its own pooled context and ran its own query. The 31 July load-test baseline measured
/// that exactly: 1,032 database reads for 30 roster polls plus 13 sweeps over 8 sessions - (30 + 13) x 8 x 3,
/// no remainder - and named that monitor as the resource that gave first, at roughly five concurrent viewers.
///
/// THE DISTINCTION THAT MUST NOT BE LOST is absent versus present-with-a-null-deadline. A row that is
/// absent means no hold at all; a row that is present with a null <c>SnoozeUntilUtc</c> is a DEFERRED hold -
/// asked for while the agent was working, clock not started, because the clock starts when the work ends.
/// A <c>Dictionary&lt;string, DateTime&gt;</c>, or a "zero means none" convention, would merge those two into
/// one answer and a deferred hold would read on the phone as no hold at all. So the map is keyed to a
/// NULLABLE deadline and presence is carried by <c>TryGetValue</c> returning true, never by the value.
///
/// THE RULES LIVE HERE AND NOWHERE ELSE. <see cref="SnoozeRegistry"/>'s three per-session readers call the
/// same two deciders below, so a snapshot answer and a per-session answer cannot drift apart - one of them
/// would have to be changed without the other, and there is only one of them. That is pinned by a test that
/// walks every shape a row can have (absent, deferred, armed-running, armed-elapsed) and asserts the two
/// paths agree.
/// </summary>
public sealed class SnoozeHoldSnapshot
{
    /// <summary>The snapshot a fold gets when there is no registry, or when it folds nothing: every session
    /// reads as no hold. Never null, so no call site needs a null check for "no snooze store".</summary>
    public static readonly SnoozeHoldSnapshot Empty =
        new(new Dictionary<string, DateTime?>(0, StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, DateTime?> _deadlineBySessionId;

    internal SnoozeHoldSnapshot(IReadOnlyDictionary<string, DateTime?> deadlineBySessionId)
        => _deadlineBySessionId = deadlineBySessionId
            ?? throw new ArgumentNullException(nameof(deadlineBySessionId));

    /// <summary>How many hold rows this snapshot carries. Diagnostics and tests; the fold does not use it.</summary>
    public int Count => _deadlineBySessionId.Count;

    /// <summary>
    /// THE AUTHORITATIVE HOLD STATE for a session, answered from this snapshot instead of from a database
    /// read. Identical in every case to <see cref="SnoozeRegistry.HoldStateFor"/> - they share
    /// <see cref="HoldStateOf"/>.
    /// </summary>
    public string HoldStateFor(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Contracts.HoldStates.None;
        var present = _deadlineBySessionId.TryGetValue(sessionId, out var untilUtc);
        return HoldStateOf(present, untilUtc, nowUtc);
    }

    /// <summary>
    /// True when this session has an ARMED entry whose return time is at or before <paramref name="nowUtc"/>.
    /// Identical in every case to <see cref="SnoozeRegistry.IsExpired"/> - they share <see cref="IsExpiredOf"/>.
    /// </summary>
    public bool IsExpired(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var present = _deadlineBySessionId.TryGetValue(sessionId, out var untilUtc);
        return IsExpiredOf(present, untilUtc, nowUtc);
    }

    /// <summary>
    /// The absolute UTC deadline an ARMED snooze returns at, or null when there is no clock to show (no
    /// entry, or a deferred entry whose clock has not started). Identical in every case to
    /// <see cref="SnoozeRegistry.SnoozeUntilFor"/>.
    /// </summary>
    public DateTime? SnoozeUntilFor(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return _deadlineBySessionId.TryGetValue(sessionId, out var untilUtc) ? untilUtc : null;
    }

    // ---- the rules, in one place -------------------------------------------------------------------

    /// <summary>
    /// The hold state a row represents. <paramref name="present"/> is whether a row exists at all, which is
    /// NOT the same question as whether its deadline is null:
    ///   * no row                  -> None. Never asked for, or already over.
    ///   * a row with no deadline  -> DeferredHold. Asked for while the agent was working; the clock starts
    ///                                when the work ends.
    ///   * a row whose clock has elapsed -> None. The owner asked for N minutes of quiet and got them.
    ///   * a row whose clock is running  -> Held.
    /// </summary>
    internal static string HoldStateOf(bool present, DateTime? untilUtc, DateTime nowUtc)
    {
        if (!present) return Contracts.HoldStates.None;
        if (untilUtc is not DateTime deadline) return Contracts.HoldStates.DeferredHold;
        return nowUtc.ToUniversalTime() >= deadline ? Contracts.HoldStates.None : Contracts.HoldStates.Held;
    }

    /// <summary>
    /// THE ONE EXPIRY PREDICATE: an armed row (a real deadline) at or past its time. A deferred row is never
    /// expired - its clock has not started, so there is nothing to elapse - and an absent row has nothing to
    /// expire either.
    /// </summary>
    internal static bool IsExpiredOf(bool present, DateTime? untilUtc, DateTime nowUtc)
        => present && untilUtc is DateTime deadline && nowUtc.ToUniversalTime() >= deadline;
}
