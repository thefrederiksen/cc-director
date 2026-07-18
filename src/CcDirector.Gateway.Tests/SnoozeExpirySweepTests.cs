using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The snooze watchdog. As of round 2 finding 2 it RETIRES NOTHING ON TIME: the passage of a snooze's
/// clock no longer deletes its entry. An elapsed entry is the "Snooze ended" badge's only source
/// (<c>SnoozeExpired = IsExpired(entry)</c>), so deleting it ~12s after expiry erased the badge before the
/// 5s display fold or the 8s web-push poll could see it - a genuine expiry could show no badge at all. So
/// an elapsed entry now lingers as a durable returned-by-timer tombstone, retired only by an edge that
/// actually ends a snooze: work (ClearIfArmed), an owner turn, an exit, or a re-snooze overwrite.
///
/// A session still returns to "needs you" the instant its clock runs out, with no sweep - HoldStateFor
/// reports an elapsed entry as None on every read; that behaviour lives in SnoozeRegistry and is covered by
/// SnoozeRegistryTests. This sweep no longer changes anything on a pass.
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
    public async Task An_elapsed_snooze_is_NOT_retired_by_the_sweep_so_the_returned_by_timer_badge_is_durable()
    {
        // ROUND 2 FINDING 2. Inspector timing: deadline passes at 11s, the display fold runs at 10s and 15s,
        // the expiry sweep runs at 12s. If the sweep deletes the entry at 12s, the fold at 15s sees nothing
        // and stamps SnoozeExpired=false; the desktop never receives true and a genuine expiry shows NO
        // badge. Here the deadline is already in the past and the sweep runs - and the entry must SURVIVE, so
        // IsExpired stays true and the badge is durable until a consumer reads it.
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1"); // deadline already passed

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));                             // NOT retired by the passage of time
        Assert.True(reg.IsExpired("s1", _now));                      // so the badge fact is durable
        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", _now)); // and it reads needs-you, never held
    }

    [Fact]
    public async Task An_elapsed_snooze_already_reads_as_not_held_BEFORE_and_AFTER_the_sweep()
    {
        // The session is back in "needs you" the instant its time is up, with no sweep and no round trip -
        // and the sweep does not change that, nor does it delete the entry.
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", _now)); // before the sweep has run at all

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));                             // still there (durable tombstone)
        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", _now)); // still needs-you
    }

    [Fact]
    public async Task A_deferred_snooze_is_never_expired_or_retired()
    {
        // A deferral has no clock, because the clock starts when the work ENDS. There is nothing to elapse,
        // so there is nothing to retire, and the sweep leaves it exactly as it found it.
        var (reg, sweep) = Make(_now.AddYears(1)); // arbitrarily far in the future
        reg.SnoozeDeferred("s1", 720, "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));
        Assert.True(Assert.Single(reg.Entries()).IsDeferred);
        Assert.Equal(HoldStates.DeferredHold, reg.HoldStateFor("s1", _now.AddYears(1)));
    }

    [Fact]
    public async Task Every_entry_is_left_untouched_by_a_pass()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("elapsed", _now.AddMinutes(-1), "dir-1");
        reg.Snooze("running", _now.AddHours(2), "dir-1");
        reg.SnoozeDeferred("deferred", 720, "dir-2");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("elapsed"));  // no longer retired on time
        Assert.True(reg.Contains("running"));
        Assert.True(reg.Contains("deferred"));
    }

    // ---------- the elapsed tombstone is cleared only by an end-of-snooze edge ----------

    [Fact]
    public void Work_clears_an_elapsed_tombstone_and_the_badge_with_it()
    {
        // An elapsed entry is armed (not deferred), so the working edge's ClearIfArmed removes it - the
        // session comes back as a plain red "needs you" with no lingering badge.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", _now));

        Assert.True(reg.ClearIfArmed("s1"));
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public void A_re_snooze_clears_an_elapsed_tombstone_and_arms_a_fresh_clock()
    {
        // Re-snooze overwrites the elapsed entry with a future deadline: IsExpired goes false (badge clears)
        // and the new snooze is armed and running.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", _now));

        reg.Snooze("s1", _now.AddHours(12), "dir-1");

        Assert.False(reg.IsExpired("s1", _now));                      // badge cleared
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("s1", _now));  // fresh snooze armed
    }

    [Fact]
    public void An_owner_turn_clears_an_elapsed_tombstone()
    {
        // The owner came back and drove a turn after the hold was set: the hold is over and the entry (and
        // its badge) is dropped.
        var reg = new SnoozeRegistry(Path_);
        var baseline = _now.AddMinutes(-30);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1", ownerTurnBaselineUtc: baseline);
        Assert.True(reg.IsExpired("s1", _now));

        Assert.True(reg.ClearIfSupersededByOwnerTurn("s1", baseline.AddMinutes(1)));
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public async Task Cancellation_stops_the_pass()
    {
        var (reg, sweep) = Make(_now);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await sweep.RunOnceAsync(cts.Token);

        Assert.True(reg.Contains("s1")); // untouched (and untouched by a normal pass too, now)
    }
}
