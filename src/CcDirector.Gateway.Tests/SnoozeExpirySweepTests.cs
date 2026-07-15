using CcDirector.Core.Sessions;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the snooze watchdog (Snooze Length mission). The sweep reads each pending snooze's
/// owning Director RAW HOLD STATE and decides: leave a not-yet-expired hold; nudge an expired hold on a
/// LIVE Director off hold (keeping the entry until the Director confirms); clear when the Director
/// reports the session already came back (issue #470 early return, or a prior nudge that took); LAND a
/// deferred hold the moment the Director reports it parked; and NEVER touch an entry whose Director is
/// unreachable (the dead-man's-switch). All Director I/O is injected, so no real Director is needed and
/// the expiry boundary is deterministic.
///
/// THESE TESTS USED TO DEFEND DEFECT 20 (rewritten 14 July 2026). The Director seam was a
/// <c>bool?</c> - "is it held?" - and two tests asserted that a read of FALSE clears the entry. That is
/// true of <see cref="HoldState.None"/> and CATASTROPHIC for <see cref="HoldState.DeferredHold"/>, which
/// also reports OnHold=false because it is not parked yet: it meant an agent-requested snooze had its
/// 12-hour timer deleted 15 seconds after it was asked for, and so never expired. The tests were green,
/// and being green is how the defect survived - they encoded the lossy boolean the bug lived in. The seam
/// now carries the tri-state and these tests name which state they mean. THE LAW they defend now: a
/// snooze is cleared ONLY on a true None, never on a DeferredHold, and never merely because something
/// reads "not held". See docs/new_architecture/session-state.html.
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

    // Build a sweep whose Director reads are driven by a dictionary: sid -> raw HoldState (None / Held /
    // DeferredHold, or null = absent/missed read), and whose Directors are all reachable unless listed in
    // unreachable. Returns the registry (to arrange entries and assert on), the sweep, and the list of
    // sids nudged off hold.
    private (SnoozeRegistry reg, SnoozeExpirySweep sweep, List<string> nudged) Make(
        DateTime now, Dictionary<string, HoldState?> holdBySid, ISet<string>? unreachableDirectors = null)
    {
        var reg = new SnoozeRegistry(Path_);
        var nudged = new List<string>();
        var sweep = new SnoozeExpirySweep(
            reg,
            isDirectorReachable: dir => !(unreachableDirectors?.Contains(dir) ?? false),
            readHoldState: (dir, sid, ct) => Task.FromResult(holdBySid.TryGetValue(sid, out var v) ? v : (HoldState?)null),
            forwardUnhold: (dir, sid, ct) => { nudged.Add(sid); return Task.CompletedTask; },
            utcNow: () => now);
        return (reg, sweep, nudged);
    }

    [Fact]
    public async Task Future_snooze_still_held_is_left_alone()
    {
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.Held });
        reg.Snooze("s1", _now.AddMinutes(60), "dir-1"); // an hour to go

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(nudged);              // not expired -> no nudge
        Assert.True(reg.Contains("s1"));   // entry stays
    }

    [Fact]
    public async Task Expired_snooze_on_a_live_director_is_nudged_off_hold_and_the_entry_is_kept()
    {
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.Held }); // director still reports held
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");                            // already expired

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "s1" }, nudged);   // nudged the live Director off hold
        Assert.True(reg.Contains("s1"));        // KEPT - cleared only once the Director confirms None
    }

    [Fact]
    public async Task Once_the_director_reports_None_the_entry_is_cleared()
    {
        // NOTE the state: None, not "false". This test read `false` until 14 July 2026, which also matched
        // a DeferredHold and so asserted defect 20 - see the type comment.
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.None }); // came back on its own (#470) or nudge took
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.False(reg.Contains("s1"));  // cleared on the confirmed transition
        Assert.Empty(nudged);              // no nudge needed - it is already off hold
    }

    [Fact]
    public async Task Early_return_before_expiry_clears_the_entry()
    {
        // Not yet expired, but the Director already reports the user drove the session again (#470).
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.None });
        reg.Snooze("s1", _now.AddMinutes(30), "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.False(reg.Contains("s1"));  // the snooze cleared the moment the session came back
        Assert.Empty(nudged);
    }

    [Fact]
    public async Task Expired_snooze_on_an_unreachable_director_is_left_pinned()
    {
        // The dead-man's-switch: the owning Director is unreachable, so the sweep does nothing and keeps
        // the entry. The aggregation overlay surfaces the session as "needs you" from the cached roster.
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.Held },
            unreachableDirectors: new HashSet<string> { "dir-dead" });
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-dead");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(nudged);              // no forward to a dead Director
        Assert.True(reg.Contains("s1"));   // pinned - never lost
    }

    [Fact]
    public async Task A_re_snooze_during_the_pass_is_not_nudged_off_hold()
    {
        // Arrange an expired entry, but simulate the user re-snoozing exactly while the sweep reads the
        // Director: the read callback moves the entry into the future, then reports still-held. The sweep
        // must re-check the LIVE registry and NOT nudge the freshly re-snoozed session off hold.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1"); // expired at snapshot time
        var nudged = new List<string>();
        var sweep = new SnoozeExpirySweep(
            reg,
            isDirectorReachable: _ => true,
            readHoldState: (dir, sid, ct) =>
            {
                reg.Snooze(sid, _now.AddMinutes(59), "dir-1");      // re-snooze lands mid-pass -> future
                return Task.FromResult<HoldState?>(HoldState.Held); // Director still reports held
            },
            forwardUnhold: (dir, sid, ct) => { nudged.Add(sid); return Task.CompletedTask; },
            utcNow: () => _now);

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(nudged);                                  // the fresh snooze was not cancelled
        Assert.True(reg.Contains("s1"));
        Assert.False(reg.IsExpired("s1", _now));               // it now holds the future time
    }

    [Fact]
    public async Task An_absent_or_missed_read_does_not_lose_the_pending_snooze()
    {
        // A reachable Director but the read returned null (session momentarily absent / transient miss, or
        // a hold-state string this Gateway does not understand). The sweep must NOT clear on that - only
        // an explicit None clears.
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = null });
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));   // kept - a transient miss never drops a snooze
        Assert.Empty(nudged);
    }

    // ===== Defect 20: the deferred path. Every test below FAILS on the code as it stood on 14 July 2026.

    [Fact]
    public async Task A_deferred_hold_is_never_cleared_by_the_sweep()
    {
        // THE HEADLINE REGRESSION TEST. The agent snoozed its own session, so it was working, so the hold
        // DEFERRED. Before the fix the sweep read OnHold=false here, concluded "not held -> the snooze is
        // over", and deleted the entry within 15 seconds - and the snooze then landed with no clock and
        // never expired. A DeferredHold is "about to be held", NOT "not held".
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.DeferredHold });
        reg.SnoozeDeferred("s1", 720, "dir-1"); // 12 hours, asked for while working

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));   // the snooze SURVIVES - this is defect 20
        Assert.Empty(nudged);              // and nothing is nudged: there is no expiry to act on yet
    }

    [Fact]
    public async Task A_deferred_hold_never_expires_because_its_clock_has_not_started()
    {
        // A deferral has no deadline at all, so no passage of time can expire it. The clock starts when
        // the work ENDS (the owner's ruling), not when the snooze was asked for.
        var (reg, sweep, nudged) = Make(_now.AddYears(1), new() { ["s1"] = HoldState.DeferredHold });
        reg.SnoozeDeferred("s1", 1, "dir-1"); // a one-minute snooze, asked for a YEAR ago

        Assert.False(reg.IsExpired("s1", _now.AddYears(1)));

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));
        Assert.Empty(nudged);              // never nudged off a hold that has not landed
    }

    [Fact]
    public async Task The_sweep_lands_a_deferred_hold_when_the_director_reports_it_held()
    {
        // The BACKSTOP for the push seam: if the landing delta was missed, the sweep's own read of the
        // Director notices the hold is now parked and starts the clock - from NOW, the moment the work
        // ended, not from when the snooze was asked for.
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = HoldState.Held });
        reg.SnoozeDeferred("s1", 720, "dir-1"); // 12 hours, deferred

        await sweep.RunOnceAsync(CancellationToken.None);

        var entry = Assert.Single(reg.Entries());
        Assert.False(entry.IsDeferred);                            // it landed
        Assert.Equal(_now.AddMinutes(720), entry.SnoozeUntilUtc);  // 12 hours from the LANDING
        Assert.Empty(nudged);                                      // freshly armed - nowhere near expired
    }

    [Fact]
    public async Task A_landed_snooze_expires_normally_twelve_hours_after_the_work_ended()
    {
        // The whole of defect 20, end to end, at the level of the sweep: defer -> land -> the clock runs
        // -> it expires -> the session is nudged back to "needs you". Before the fix there was no clock at
        // this point, so this could never happen and the snooze was permanent.
        var landedAt = _now;
        var reg = new SnoozeRegistry(Path_);
        reg.SnoozeDeferred("s1", 720, "dir-1");
        reg.Land("s1", landedAt);

        var nudged = new List<string>();
        var later = landedAt.AddMinutes(721); // twelve hours and one minute after the work ended
        var sweep = new SnoozeExpirySweep(
            reg,
            isDirectorReachable: _ => true,
            readHoldState: (dir, sid, ct) => Task.FromResult<HoldState?>(HoldState.Held),
            forwardUnhold: (dir, sid, ct) => { nudged.Add(sid); return Task.CompletedTask; },
            utcNow: () => later);

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "s1" }, nudged);  // it came back on its own, as the owner asked
    }
}
