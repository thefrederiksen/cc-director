using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// The short retry during a release's publish window, and the bound on it (issue #1079).
///
/// The bound is the part worth testing. Shortening the wait is easy and obviously right; the failure it
/// introduces is that a release whose assets NEVER arrive - a workflow that fell over after pushing its
/// tag - stays in that state permanently, and an unbounded short poll against one would keep calling
/// GitHub every few minutes for as long as the Director runs.
/// </summary>
public class ReleaseNotReadyRetryTests
{
    private static readonly TimeSpan Ordinary = TimeSpan.FromHours(1);

    [Fact]
    public void APublishWindow_IsRetriedInMinutesRatherThanTheFullCycle()
    {
        var retry = new ReleaseNotReadyRetry();

        var delay = retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary);

        Assert.Equal(ReleaseNotReadyRetry.Interval, delay);
        Assert.True(delay < Ordinary);
    }

    [Fact]
    public void AReleaseWhoseAssetsNeverArrive_StopsBeingPolledAndFallsBackToTheOrdinaryCadence()
    {
        var retry = new ReleaseNotReadyRetry();

        for (var attempt = 1; attempt <= ReleaseNotReadyRetry.MaxConsecutive; attempt++)
            Assert.Equal(ReleaseNotReadyRetry.Interval, retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary));

        // The window is long over. Anything still reporting this is not a window, and hammering it for
        // the lifetime of the process helps nobody - the status keeps saying what it is either way.
        Assert.Equal(Ordinary, retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary));
        Assert.Equal(Ordinary, retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary));
    }

    [Fact]
    public void TheBoundIsPerEpisode_NotABudgetTheProcessSpendsOnce()
    {
        // A machine that hit one bad publish this morning must still retry quickly this afternoon.
        var retry = new ReleaseNotReadyRetry();
        for (var attempt = 0; attempt < ReleaseNotReadyRetry.MaxConsecutive + 3; attempt++)
            retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary);

        retry.NextDelay(UpdatePhase.UpToDate, Ordinary);   // any other outcome ends the episode

        Assert.Equal(0, retry.Consecutive);
        Assert.Equal(ReleaseNotReadyRetry.Interval, retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary));
    }

    [Theory]
    [InlineData(UpdatePhase.UpToDate)]
    [InlineData(UpdatePhase.Staged)]
    [InlineData(UpdatePhase.Failed)]
    [InlineData(UpdatePhase.NoBuildForThisPlatform)]
    public void EveryOtherOutcome_WaitsTheConfiguredInterval(UpdatePhase outcome)
    {
        // NoBuildForThisPlatform especially: the release is COMPLETE and simply has nothing for this
        // machine, so looking again in three minutes would poll a finished release for ever.
        Assert.Equal(Ordinary, new ReleaseNotReadyRetry().NextDelay(outcome, Ordinary));
    }

    [Fact]
    public void Reset_EndsTheEpisodeForACycleThatDidNotCheckAtAll()
    {
        // Auto-update switched off: the loop re-reads its configuration and checks nothing, so it has no
        // outcome to pace from. Without this it would keep pacing from the last one it had and spin every
        // three minutes doing no work.
        var retry = new ReleaseNotReadyRetry();
        retry.NextDelay(UpdatePhase.ReleaseNotReady, Ordinary);
        Assert.Equal(1, retry.Consecutive);

        retry.Reset();

        Assert.Equal(0, retry.Consecutive);
    }
}
