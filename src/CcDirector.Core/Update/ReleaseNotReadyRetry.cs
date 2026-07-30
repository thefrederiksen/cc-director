namespace CcDirector.Core.Update;

/// <summary>
/// How soon to look again, given what the last check concluded (issues #1030 and #1079).
///
/// A release becomes "latest" the instant its tag is pushed and its downloads are attached about five
/// and a half minutes later, so a machine that checks inside that window has found a real update it
/// cannot fetch yet. Waiting a full hour for the next ordinary cycle wastes almost all of it, so that
/// one outcome - and only that one - is retried in minutes.
///
/// THE RETRY IS BOUNDED, and the bound is the point. A release whose assets never arrive - a workflow
/// that failed after pushing its tag - is permanently manifest-less, and an unbounded short poll
/// against it would hammer GitHub every few minutes for as long as the Director runs. Five consecutive
/// attempts covers a window measured in single-digit minutes several times over; after that the machine
/// falls back to the ordinary cadence and the status keeps saying what it is, which is the part that
/// actually matters. The count resets on any other outcome, so this is per episode rather than a budget
/// the process spends once and never gets back.
///
/// The numbers match the machine-tier path's own retry, deliberately: the same situation should be
/// waited out the same way whichever code path meets it. When that path's shared policy type lands,
/// these two should become one.
/// </summary>
public sealed class ReleaseNotReadyRetry
{
    /// <summary>How long to wait before looking again during a publish window.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    /// <summary>How many consecutive short retries one episode is allowed before giving up on it.</summary>
    public const int MaxConsecutive = 5;

    private int _consecutive;

    /// <summary>How many short retries have been taken in the current episode.</summary>
    public int Consecutive => _consecutive;

    /// <summary>
    /// End the current episode without a check having happened. Used by a cycle that did not look at
    /// all - auto-update switched off - which has no outcome to pace from and must not keep pacing from
    /// the last one it had.
    /// </summary>
    public void Reset() => _consecutive = 0;

    /// <summary>
    /// The wait after a check that concluded <paramref name="outcome"/>, given
    /// <paramref name="ordinaryInterval"/> as the configured cadence. Returns the short interval only
    /// while a publish window is plausibly still open.
    /// </summary>
    public TimeSpan NextDelay(UpdatePhase outcome, TimeSpan ordinaryInterval)
    {
        if (outcome != UpdatePhase.ReleaseNotReady)
        {
            _consecutive = 0;
            return ordinaryInterval;
        }

        if (_consecutive >= MaxConsecutive)
            return ordinaryInterval;

        _consecutive++;
        return Interval;
    }
}
