using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The snooze watchdog, which now retires entries whose clock has run out and does nothing else.
///
/// WHAT THIS FILE USED TO TEST. The sweep read each pending snooze's owning Director over the tunnel,
/// interpreted its raw hold, landed deferrals the push seam had missed, nudged live Directors off hold on
/// expiry, kept entries until a Director confirmed, and never touched an entry whose Director was
/// unreachable (the dead-man's-switch). Twelve tests covered that protocol. It is all deleted, along with
/// the reason it existed: the state lived on a Director and the clock lived on the Gateway, so expiry was
/// a negotiation between two processes.
///
/// The Gateway owns both now, so expiry is a local fact and needs no protocol. Note what that does to
/// defect 20 - the boolean read that deleted a twelve-hour timer 15 seconds after it was asked for. It is
/// not defended here any more; it is UNREACHABLE, because this sweep never asks anybody whether a session
/// is held. The best fix for a bug in a conversation between two processes is to stop having it.
///
/// The "does a session come back when its snooze is up" behaviour did not move into a gap - it moved to
/// SnoozeRegistry.HoldStateFor, which reports an elapsed entry as not-held on every read, and is covered
/// by SnoozeRegistryTests. This sweep only stops the registry growing.
/// </summary>
public sealed class SnoozeExpirySweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-snoozesweep-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private string Path_ => System.IO.Path.Combine(_dir, "snooze.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private (SnoozeRegistry reg, SnoozeExpirySweep sweep) Make(DateTime now)
    {
        var reg = new SnoozeRegistry(Path_);
        return (reg, new SnoozeExpirySweep(reg, utcNow: () => now));
    }

    [Fact]
    public async Task A_future_snooze_is_left_alone()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(60), "dir-1"); // an hour to go

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("s1", _now));
    }

    [Fact]
    public async Task An_expired_snooze_is_retired()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1"); // up a minute ago

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public async Task An_expired_snooze_already_reads_as_not_held_BEFORE_the_sweep_runs()
    {
        // The sweep is bookkeeping, not correctness. This is the property that makes it so: the session is
        // back in "needs you" the instant its time is up, with no sweep, no round trip, and no Director.
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", _now)); // before the sweep has run at all

        await sweep.RunOnceAsync(CancellationToken.None);
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public async Task A_deferred_snooze_is_never_expired_or_retired()
    {
        // DEFECT 20's case, now unreachable by construction. A deferral has no clock, because the clock
        // starts when the work ENDS. There is nothing to elapse, so there is nothing to retire - and the
        // sweep cannot mistake it for "not held" because it never asks that question of anyone.
        var (reg, sweep) = Make(_now.AddYears(1)); // arbitrarily far in the future
        reg.SnoozeDeferred("s1", 720, "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));
        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
        Assert.Equal(HoldStates.DeferredHold, reg.HoldStateFor("s1", _now.AddYears(1)));
    }

    [Fact]
    public async Task A_snooze_on_a_dead_director_still_returns_on_time()
    {
        // The dead-man's-switch is not needed any more, because the thing it protected against cannot
        // happen: the hold never lived on the Director, so a Director dying cannot strand it. There is no
        // reachability check left in the sweep at all - this test exists to pin that a hold owned by a
        // Director that never comes back still expires exactly on schedule.
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-that-is-long-dead");

        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", _now));

        await sweep.RunOnceAsync(CancellationToken.None);
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public async Task A_re_snooze_during_the_pass_is_not_destroyed_by_a_stale_expiry()
    {
        // Compare-and-clear: the pass snapshots the entries, and a snooze that lands while it is running
        // must win over the decision the pass made from the older value.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1"); // expired as the pass begins

        var sweep = new SnoozeExpirySweep(reg, utcNow: () =>
        {
            // The user re-snoozes for another hour in the instant between the snapshot and the decision.
            reg.Snooze("s1", _now.AddHours(1), "dir-1");
            return _now;
        });

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1")); // the fresh snooze survived
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("s1", _now));
    }

    [Fact]
    public async Task Every_entry_is_handled_independently()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("expired", _now.AddMinutes(-1), "dir-1");
        reg.Snooze("running", _now.AddHours(2), "dir-1");
        reg.SnoozeDeferred("deferred", 720, "dir-2");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.False(reg.Contains("expired"));
        Assert.True(reg.Contains("running"));
        Assert.True(reg.Contains("deferred"));
    }

    [Fact]
    public async Task Cancellation_stops_the_pass()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await sweep.RunOnceAsync(cts.Token);

        Assert.True(reg.Contains("s1")); // untouched
    }
}
