namespace CcDirector.Setup.Engine;

/// <summary>
/// How long an update loop waits after finding the latest release published but incomplete
/// (see <see cref="ReleaseNotReadyException"/>), instead of waiting out its normal cycle.
///
/// The problem this solves: the update check runs roughly hourly, and the incomplete-release
/// window was measured at five and a half minutes. A machine that checked inside it lost up to an
/// HOUR waiting for the next ordinary cycle, for a condition that resolves itself in minutes.
///
/// The cadence is deliberately bounded. Retrying for a quarter of an hour covers a window
/// measured at 5m23s with room to spare; a release still missing its manifest after fifteen
/// minutes is not a window any more, it is a broken release, and hammering GitHub every three
/// minutes forever would neither fix it nor be polite. After that the loop returns to its normal
/// interval - having said, in the log, exactly what it saw.
///
/// One policy object per loop, held across cycles: <see cref="NextDelay"/> on a not-ready result
/// and <see cref="Reset"/> on any other outcome, so the allowance is per EPISODE rather than a
/// budget the process spends once and never gets back.
/// </summary>
public sealed class ReleaseNotReadyRetry
{
    /// <summary>How long to wait before looking again while a release is still being completed.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many consecutive short retries are allowed before falling back to the normal cycle.
    /// Five at three minutes is fifteen minutes of cover for a five-and-a-half minute window.
    /// </summary>
    public const int MaxConsecutive = 5;

    private int _consecutive;

    /// <summary>Consecutive not-ready results seen so far in this episode.</summary>
    public int Consecutive => _consecutive;

    /// <summary>
    /// Records a not-ready result and returns how long to wait before looking again, or null when
    /// the short-retry allowance for this episode is used up and the caller should fall back to its
    /// normal interval.
    /// </summary>
    public TimeSpan? NextDelay()
    {
        _consecutive++;
        return _consecutive <= MaxConsecutive ? Interval : null;
    }

    /// <summary>
    /// Clears the episode. Called on every outcome that is NOT not-ready, so a later incomplete
    /// release gets the full allowance again rather than inheriting an exhausted one.
    /// </summary>
    public void Reset() => _consecutive = 0;

    /// <summary>
    /// The log line for a not-ready result. It states the cause, the wait, and that nothing is
    /// wrong - the three things missing from "update check failed", which is what this used to say.
    /// </summary>
    public string Describe(ReleaseNotReadyException ex, TimeSpan? delay) =>
        delay is { } d
            ? $"release {ex.Tag} is published but its {ex.MissingAsset} is not attached yet " +
              $"(assets still uploading); NOT a failure - looking again in {d.TotalMinutes:0} minutes " +
              $"(retry {_consecutive}/{MaxConsecutive})"
            : $"release {ex.Tag} STILL has no {ex.MissingAsset} after {MaxConsecutive} short retries " +
              $"({MaxConsecutive * Interval.TotalMinutes:0} minutes). That is no longer a publish window - " +
              "the release looks incomplete. Falling back to the normal check interval.";
}
