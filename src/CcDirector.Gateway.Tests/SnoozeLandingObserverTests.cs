using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The push seam, where the Gateway drives the hold machine from the facts a Director reports.
///
/// These tests used to feed the observer a Director's HOLD state and assert it believed it. That is the
/// architecture this replaces: a Director does not decide hold. It reports the one thing only it can see -
/// whether it is working - and the GATEWAY rules on what that means for the owner's hold.
///
/// THE RULING (owner, 14 July 2026) still stands and is what the landing edge implements: the clock starts
/// when the work ENDS. "Snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it
/// finishes.
/// </summary>
public sealed class SnoozeLandingObserverTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly DateTime _now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private SnoozeRegistry NewReg() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose() => _h.Dispose();

    private (SnoozeRegistry reg, SnoozeLandingObserver obs) Make(DateTime? now = null)
    {
        var reg = NewReg();
        return (reg, new SnoozeLandingObserver(reg, () => now ?? _now));
    }

    /// <summary>A session as its Director reports it: an activity, and optionally an owner turn.</summary>
    private static SessionDto Session(string sid, string activityState, DateTime? ownerTurn = null) =>
        new() { SessionId = sid, ActivityState = activityState, LastOwnerTurnAtUtc = ownerTurn };

    [Fact]
    public void WorkEnding_LandsTheDeferral_AndStartsTheClockFromThatInstant()
    {
        // The agent's turn just ended. THAT is when the twelve hours start - not when the snooze was
        // asked for. The Director said only "I am waiting for input"; the Gateway drew the conclusion.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", "WaitingForInput"));

        var entry = Assert.Single(reg.Entries());
        Assert.False(entry.IsDeferred);
        Assert.Equal(_now.AddMinutes(720), entry.SnoozeUntilUtc);
        Assert.Null(entry.PendingMinutes);
    }

    [Theory]
    [InlineData("WaitingForPerm")]
    [InlineData("Idle")]
    public void AnySettledState_LandsTheDeferral(string activity)
    {
        // Settled is "the work has ended", not "it is sitting at a prompt": a session blocked on a
        // permission answer has finished the turn the deferral was waiting for.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", activity));

        Assert.False(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void StillWorking_ChangesNothing()
    {
        // No clock starts while the work is running: that is the whole meaning of "snooze me when this
        // finishes".
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", "Working"));

        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void ALandedHoldSurvivesTheSessionWorkingAgain()
    {
        // THE DEFECT THIS ARCHITECTURE EXISTS TO KILL. Activity is not consent. On 15 July 2026 all
        // sixteen holds died within 1-21 minutes to exactly this edge, because "it started working" was
        // treated as "the owner wants it back". Another agent's fleet message is real work; so is a bare
        // terminal repaint. Neither is the owner.
        var (reg, obs) = Make();
        reg.Snooze("s1", _now.AddHours(12), "dir-1");

        obs.Observe(Session("s1", "Working"));

        var entry = Assert.Single(reg.Entries());
        Assert.Equal(_now.AddHours(12), entry.SnoozeUntilUtc);
    }

    [Fact]
    public void AnOwnerTurnAfterTheRequest_DropsTheHold()
    {
        // The owner came back and typed. They are demonstrably not away, so there is nobody to avoid
        // bothering. This is one of only four ways a hold ends.
        var (reg, obs) = Make();
        var baseline = DateTime.UtcNow;
        reg.Snooze("s1", _now.AddHours(12), "dir-1", ownerTurnBaselineUtc: baseline);

        obs.Observe(Session("s1", "Working", ownerTurn: baseline.AddSeconds(5))); // a NEW turn, same clock

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void AnOwnerTurnFromBEFORETheRequest_DoesNotDropTheHold()
    {
        // The stamp is a high-water mark, not an event: a session the owner typed into an hour ago still
        // carries that timestamp. Reading it as "the owner is back" would make a hold impossible to set on
        // any session the owner had ever touched - which is all of them.
        var (reg, obs) = Make();
        var typedAnHourAgo = DateTime.UtcNow.AddHours(-1);
        reg.Snooze("s1", _now.AddHours(12), "dir-1", ownerTurnBaselineUtc: typedAnHourAgo);

        obs.Observe(Session("s1", "Working", ownerTurn: typedAnHourAgo)); // unchanged since the hold was set

        Assert.Single(reg.Entries());
    }

    [Fact]
    public void ADirectorWhoseClockRunsFast_DoesNotInstantlyKillEveryHold()
    {
        // THE SHIP-BLOCKER, caught in review. The baseline and the turn stamp must both come from the
        // SAME clock - the Director's. The obvious implementation compared the Director's turn stamp
        // against a GATEWAY-stamped request time; a Director running an hour fast then reported a turn
        // "in the future" relative to that request, read as "the owner is back", and killed the hold the
        // instant it was set. On every session on that machine. Forever.
        //
        // Here the Director's clock is an hour ahead of the Gateway's and its last owner turn is stale by
        // its OWN reckoning. Nothing about the two machines disagreeing may matter.
        var (reg, obs) = Make();
        var directorClockIsAnHourFast = DateTime.UtcNow.AddHours(1);
        reg.Snooze("s1", _now.AddHours(12), "dir-1", ownerTurnBaselineUtc: directorClockIsAnHourFast);

        obs.Observe(Session("s1", "Working", ownerTurn: directorClockIsAnHourFast)); // no NEW turn

        Assert.Single(reg.Entries()); // still held
    }

    [Fact]
    public void ANullBaseline_MeansAnyOwnerTurnIsNews()
    {
        // The owner had never driven a turn when the hold was set, so the first one they drive is them
        // coming back.
        var (reg, obs) = Make();
        reg.Snooze("s1", _now.AddHours(12), "dir-1", ownerTurnBaselineUtc: null);

        obs.Observe(Session("s1", "Working", ownerTurn: DateTime.UtcNow));

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void AnOwnerTurn_SupersedesADeferralThatHasNotLandedYet()
    {
        // The owner's own rule: "if you type into a turn and the hold had already been set, it's not the
        // same turn, it's a new turn, so no it should not hold." Discarded, not deferred onward.
        var (reg, obs) = Make();
        var baseline = DateTime.UtcNow;
        reg.SnoozeDeferred("s1", 720, "dir-1", ownerTurnBaselineUtc: baseline);

        obs.Observe(Session("s1", "Working", ownerTurn: baseline.AddSeconds(5))); // a NEW turn, same clock

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void AnExitedSession_DropsTheHold()
    {
        // A dead session must never hide behind a "Snoozed" label. The Director reports the exit; the
        // Gateway drops the hold.
        var (reg, obs) = Make();
        reg.Snooze("s1", _now.AddHours(12), "dir-1");

        obs.Observe(Session("s1", "Exited"));

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void APushForASessionWithNoHoldIsANoOp()
    {
        var (reg, obs) = Make();

        obs.Observe(Session("s-unknown", "WaitingForInput"));

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void LandingIsIdempotent_ARunningClockIsNeverRestarted()
    {
        // A settled session pushes its state repeatedly. If each push re-landed the deferral, the snooze's
        // return would move further away every time and never arrive.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", "WaitingForInput"));
        var first = Assert.Single(reg.Entries()).SnoozeUntilUtc;

        var later = new SnoozeLandingObserver(reg, () => _now.AddHours(3));
        later.Observe(Session("s1", "WaitingForInput"));

        Assert.Equal(first, Assert.Single(reg.Entries()).SnoozeUntilUtc);
    }

    [Fact]
    public void AnUnrecognisedActivity_NeverLandsADeferral()
    {
        // Landing starts a clock, and a clock started too early expires too early. An activity we do not
        // understand is not evidence that the work ended.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", "SomethingNewNobodyTaughtUs"));

        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void StartingIsNotSettled()
    {
        // A session still coming up has not finished anything yet.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.Observe(Session("s1", "Starting"));

        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void ASnapshotLandsADeferralThatArrivedWhileTheDirectorWasDisconnected()
    {
        // A Director that reconnects sends a full snapshot, not deltas - so a turn that ended while it was
        // away arrives only there.
        var (reg, obs) = Make();
        reg.SnoozeDeferred("s1", 720, "dir-1");

        obs.ObserveSnapshot(new[] { Session("other", "Working"), Session("s1", "WaitingForInput") });

        Assert.False(Assert.Single(reg.Entries()).IsDeferred);
    }

    [Fact]
    public void NullAndEmptyInputsAreIgnored()
    {
        var (reg, obs) = Make();

        obs.Observe(null);
        obs.Observe(new SessionDto { SessionId = "", ActivityState = "WaitingForInput" });
        obs.ObserveSnapshot(null);

        Assert.Empty(reg.Entries());
    }
}
