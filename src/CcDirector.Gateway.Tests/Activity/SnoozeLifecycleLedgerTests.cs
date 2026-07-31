using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Activity;

/// <summary>
/// The Gateway records its OWN snooze decisions in the durable activity ledger - created, landed, ended,
/// each with the WHY. This is the direct product of the July 24 incident: an armed eight-hour snooze was
/// deleted by a working observation and the deletion's reason existed only in free-text logs. With these
/// events, that incident is self-explanatory from structured history.
///
/// One honesty rule is proven here specifically: when an ARMED entry whose deadline has already ELAPSED is
/// retired by some edge, the recorded cause is TIMER-EXPIRED (the timer ended that snooze; the edge only
/// cleaned up the tombstone), with the retiring edge preserved in the detail.
/// </summary>
public sealed class SnoozeLifecycleLedgerTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly ActivityEventStore _ledger;
    private readonly SnoozeRegistry _registry;

    public SnoozeLifecycleLedgerTests()
    {
        var db = _harness.Open();
        _ledger = new ActivityEventStore(db);
        _registry = new SnoozeRegistry(db, _harness.LegacyPath("snooze.json"), _ledger);
    }

    public void Dispose() => _harness.Dispose();

    private IReadOnlyList<ActivityEventRecord> EventsFor(string sessionId)
        => _ledger.Read(sessionId: sessionId);

    [Fact]
    public void Arming_a_snooze_records_created_with_the_deadline()
    {
        var until = DateTime.UtcNow.AddHours(8);
        _registry.Snooze("s1", until, "dir-1");

        var e = Assert.Single(EventsFor("s1"));
        Assert.Equal(ActivityEventTypes.SnoozeCreated, e.EventType);
        Assert.Equal(ActivityCauses.SnoozeRequested, e.Cause);
        Assert.Equal(HoldStates.None, e.PreviousState);
        Assert.Equal(HoldStates.Held, e.NewState);
        Assert.Equal("dir-1", e.DirectorId);
        Assert.Contains(until.ToUniversalTime().ToString("O"), e.Detail);
    }

    [Fact]
    public void Deferring_a_snooze_records_created_with_the_length()
    {
        _registry.SnoozeDeferred("s1", 480, "dir-1");

        var e = Assert.Single(EventsFor("s1"));
        Assert.Equal(ActivityEventTypes.SnoozeCreated, e.EventType);
        Assert.Equal(HoldStates.DeferredHold, e.NewState);
        Assert.Equal("minutes=480", e.Detail);
    }

    [Fact]
    public void Landing_a_deferral_records_landed_with_the_started_clock()
    {
        _registry.SnoozeDeferred("s1", 60, "dir-1");
        _registry.Land("s1", DateTime.UtcNow);

        var landed = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeLanded);
        Assert.Equal(ActivityCauses.WorkSettled, landed.Cause);
        Assert.Equal(HoldStates.DeferredHold, landed.PreviousState);
        Assert.Equal(HoldStates.Held, landed.NewState);
        Assert.Contains("minutes=60", landed.Detail);
    }

    [Fact]
    public void A_working_observation_deleting_an_armed_snooze_records_the_july_24_cause()
    {
        // The incident shape: an armed snooze with hours still on the clock, deleted because the session
        // was reported Working. The durable reason must say exactly that.
        _registry.Snooze("s1", DateTime.UtcNow.AddHours(8), "dir-1");
        Assert.True(_registry.ClearIfArmed("s1"));

        var ended = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded);
        Assert.Equal(ActivityCauses.WorkingObservation, ended.Cause);
        Assert.Equal(HoldStates.Held, ended.PreviousState);
        Assert.Equal(HoldStates.None, ended.NewState);
    }

    [Fact]
    public void Retiring_an_already_elapsed_snooze_records_timer_expired_not_the_retiring_edge()
    {
        // The snooze ended at its deadline; a later working observation only cleans up the tombstone. The
        // cause is the timer, and the edge is preserved as detail - the distinction the incident's
        // "waiting 1h 5m" confusion needed.
        _registry.Snooze("s1", DateTime.UtcNow.AddMinutes(-5), "dir-1");
        Assert.True(_registry.ClearIfArmed("s1"));

        var ended = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded);
        Assert.Equal(ActivityCauses.TimerExpired, ended.Cause);
        Assert.Contains($"retired-by={ActivityCauses.WorkingObservation}", ended.Detail);
    }

    [Fact]
    public void A_manual_release_records_manual_release()
    {
        _registry.Snooze("s1", DateTime.UtcNow.AddHours(1), "dir-1");
        Assert.True(_registry.Clear("s1", ActivityCauses.ManualRelease));

        var ended = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded);
        Assert.Equal(ActivityCauses.ManualRelease, ended.Cause);
    }

    [Fact]
    public void An_owner_turn_records_owner_turn()
    {
        var baseline = DateTime.UtcNow.AddMinutes(-30);
        _registry.Snooze("s1", DateTime.UtcNow.AddHours(1), "dir-1", ownerTurnBaselineUtc: baseline);
        Assert.True(_registry.ClearIfSupersededByOwnerTurn("s1", baseline.AddMinutes(1)));

        var ended = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded);
        Assert.Equal(ActivityCauses.OwnerTurn, ended.Cause);
    }

    [Fact]
    public void A_session_exit_clear_records_session_exit()
    {
        _registry.Snooze("s1", DateTime.UtcNow.AddHours(1), "dir-1");
        Assert.True(_registry.Clear("s1", ActivityCauses.SessionExit));

        var ended = Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded);
        Assert.Equal(ActivityCauses.SessionExit, ended.Cause);
    }

    // NOTE THE NAME, WHICH NO LONGER DESCRIBES A PRODUCTION EVENT. Nothing calls ClearForDirector on a
    // director removal any more - that subscriber was deleted with the eviction cascade (epic #1159 step A,
    // inspection 2 finding 1), so the DirectorRemoved ledger cause below is reachable only by calling the
    // primitive directly, as this test does. The ledger behaviour is still worth pinning for whatever
    // future cleanup calls it; it is NOT evidence that an eviction clears snoozes, and it must not be read
    // as a reason to re-wire it.
    [Fact]
    public void A_director_removal_records_one_ended_event_per_entry()
    {
        _registry.Snooze("s1", DateTime.UtcNow.AddHours(1), "dir-1");
        _registry.Snooze("s2", DateTime.UtcNow.AddHours(2), "dir-1");
        Assert.Equal(2, _registry.ClearForDirector("dir-1"));

        Assert.Equal(ActivityCauses.DirectorRemoved,
            Assert.Single(EventsFor("s1"), e => e.EventType == ActivityEventTypes.SnoozeEnded).Cause);
        Assert.Equal(ActivityCauses.DirectorRemoved,
            Assert.Single(EventsFor("s2"), e => e.EventType == ActivityEventTypes.SnoozeEnded).Cause);
    }

    [Fact]
    public void A_registry_without_a_ledger_still_works()
    {
        // The ledger OBSERVES the snooze machine; a registry built without one (older tests, callers that
        // opted out) behaves exactly as before.
        var bare = new SnoozeRegistry(_harness.Open(), _harness.LegacyPath("snooze-bare.json"));
        bare.Snooze("s9", DateTime.UtcNow.AddHours(1), "dir-9");
        Assert.True(bare.Clear("s9", ActivityCauses.ManualRelease));
        Assert.False(bare.Contains("s9"));
    }
}
