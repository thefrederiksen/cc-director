using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 20, the PUSH seam: a deferred snooze's clock starts the moment the hold LANDS, which the
/// Director tells the Gateway on its own (Session.HoldStateChanged fires on DeferredHold -&gt; Held and
/// the Control API pushes a delta for it). THE RULING (owner, 14 July 2026): the clock starts when the
/// work ENDS - "snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes.
/// See docs/new_architecture/session-state.html.
/// </summary>
public sealed class SnoozeLandingObserverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-snoozeland-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private string Path_ => System.IO.Path.Combine(_dir, "snooze.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private (SnoozeRegistry reg, SnoozeLandingObserver obs) Make(DateTime? now = null)
    {
        var reg = new SnoozeRegistry(Path_);
        return (reg, new SnoozeLandingObserver(reg, () => now ?? _now));
    }

    private static SessionDto Session(string sid, string holdState) =>
        new() { SessionId = sid, HoldState = holdState };

    [Fact]
    public void APushedHeldSessionLandsItsDeferredSnoozeAndStartsTheClockFromNow()
    {
        // THE REGRESSION, at the seam: the agent's turn just ended, the Director pushed up "the hold
        // landed", and THAT is the instant the twelve hours start - not when the snooze was asked for.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", HoldStates.Held));

        var entry = Assert.Single(reg.Entries());
        Assert.False(entry.IsDeferred);
        Assert.Equal(_now.AddMinutes(720), entry.SnoozeUntilUtc);
        Assert.Null(entry.PendingMinutes);
    }

    [Fact]
    public void AStillDeferredPushChangesNothing()
    {
        // The agent is still working. The hold has not landed, so no clock starts: that is the whole
        // meaning of "snooze me when this finishes".
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", HoldStates.DeferredHold));

        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void ANonePushIsIgnoredHere_ClearingIsTheSweepsJob()
    {
        // The observer only ever LANDS. Clearing a snooze is the sweep's single responsibility, and it
        // clears only on a confirmed None - keeping the two decisions in one place each.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", HoldStates.None));

        Assert.True(reg.Contains("s1"));
    }

    [Fact]
    public void APushForASessionWithNoSnoozeIsANoOp()
    {
        var (reg, obs) = Make();

        obs.Observe(Session("s-unknown", HoldStates.Held));

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void LandingIsIdempotent_ARunningClockIsNeverRestarted()
    {
        // Two things land a deferral - this push seam and the sweep's backstop - so whichever arrives
        // first must win and the other must change nothing. A landing that restarted the clock would push
        // the snooze's return further away every time the Director re-pushed.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", HoldStates.Held));
        var first = Assert.Single(reg.Entries()).SnoozeUntilUtc;

        var later = new SnoozeLandingObserver(reg, () => _now.AddHours(3));
        later.Observe(Session("s1", HoldStates.Held));

        Assert.Equal(first, Assert.Single(reg.Entries()).SnoozeUntilUtc);
    }

    [Fact]
    public void ASnapshotLandsADeferralThatArrivedWhileTheDirectorWasDisconnected()
    {
        // A Director that reconnects sends a full snapshot, not deltas - so a hold that landed while it
        // was away arrives only there. Without watching the snapshot the landing waits for the sweep.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.ObserveSnapshot(new[] { Session("other", HoldStates.None), Session("s1", HoldStates.Held) });

        Assert.False(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void NullAndEmptyInputsAreIgnored()
    {
        var (reg, obs) = Make();

        obs.Observe(null);
        obs.Observe(new SessionDto { SessionId = "", HoldState = HoldStates.Held });
        obs.ObserveSnapshot(null);

        Assert.Empty(reg.Entries());
    }
}
