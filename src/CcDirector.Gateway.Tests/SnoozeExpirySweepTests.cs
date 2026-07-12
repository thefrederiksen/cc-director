using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the snooze watchdog (Snooze Length mission). The sweep reads each pending snooze's
/// owning Director RAW state and decides: leave a not-yet-expired hold; nudge an expired hold on a
/// LIVE Director off hold (keeping the entry until the Director confirms); clear when the Director
/// reports the session already came back (issue #470 early return, or a prior nudge that took); and
/// NEVER touch an entry whose Director is unreachable (the dead-man's-switch). All Director I/O is
/// injected, so no real Director is needed and the expiry boundary is deterministic.
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

    // Build a sweep whose Director reads are driven by a dictionary: sid -> raw OnHold (true held, false
    // not held, null absent), and whose Directors are all reachable unless listed in unreachable. Returns
    // the registry (to arrange entries and assert on), the sweep, and the list of sids nudged off hold.
    private (SnoozeRegistry reg, SnoozeExpirySweep sweep, List<string> nudged) Make(
        DateTime now, Dictionary<string, bool?> onHoldBySid, ISet<string>? unreachableDirectors = null)
    {
        var reg = new SnoozeRegistry(Path_);
        var nudged = new List<string>();
        var sweep = new SnoozeExpirySweep(
            reg,
            resolveEndpoint: dir => (unreachableDirectors?.Contains(dir) ?? false) ? null : $"http://{dir}",
            readOnHold: (ep, sid, ct) => Task.FromResult(onHoldBySid.TryGetValue(sid, out var v) ? v : (bool?)null),
            forwardUnhold: (ep, sid, ct) => { nudged.Add(sid); return Task.CompletedTask; },
            utcNow: () => now);
        return (reg, sweep, nudged);
    }

    [Fact]
    public async Task Future_snooze_still_held_is_left_alone()
    {
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = true });
        reg.Snooze("s1", _now.AddMinutes(60), "dir-1"); // an hour to go

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(nudged);              // not expired -> no nudge
        Assert.True(reg.Contains("s1"));   // entry stays
    }

    [Fact]
    public async Task Expired_snooze_on_a_live_director_is_nudged_off_hold_and_the_entry_is_kept()
    {
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = true }); // director still reports held
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");                 // already expired

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "s1" }, nudged);   // nudged the live Director off hold
        Assert.True(reg.Contains("s1"));        // KEPT - cleared only once the Director confirms not-held
    }

    [Fact]
    public async Task Once_the_director_reports_not_held_the_entry_is_cleared()
    {
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = false }); // came back on its own (#470) or nudge took
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.False(reg.Contains("s1"));  // cleared on the confirmed transition
        Assert.Empty(nudged);              // no nudge needed - it is already off hold
    }

    [Fact]
    public async Task Early_return_before_expiry_clears_the_entry()
    {
        // Not yet expired, but the Director already reports the user drove the session again (#470).
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = false });
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
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = true },
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
            resolveEndpoint: _ => "http://dir-1",
            readOnHold: (ep, sid, ct) =>
            {
                reg.Snooze(sid, _now.AddMinutes(59), "dir-1"); // re-snooze lands mid-pass -> future
                return Task.FromResult<bool?>(true);           // Director still reports held
            },
            forwardUnhold: (ep, sid, ct) => { nudged.Add(sid); return Task.CompletedTask; },
            utcNow: () => _now);

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(nudged);                                  // the fresh snooze was not cancelled
        Assert.True(reg.Contains("s1"));
        Assert.False(reg.IsExpired("s1", _now));               // it now holds the future time
    }

    [Fact]
    public async Task An_absent_or_missed_read_does_not_lose_the_pending_snooze()
    {
        // A reachable Director but the read returned null (session momentarily absent / transient miss).
        // The sweep must NOT clear on that - only an explicit not-held clears.
        var (reg, sweep, nudged) = Make(_now, new() { ["s1"] = null });
        reg.Snooze("s1", _now.AddMinutes(-1), "dir-1");

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.True(reg.Contains("s1"));   // kept - a transient miss never drops a snooze
        Assert.Empty(nudged);
    }
}
