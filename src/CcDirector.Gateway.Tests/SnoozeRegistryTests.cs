using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Gateway-owned snooze registry (Snooze Length mission): the persisted
/// <c>sessionId -&gt; SnoozeUntilUtc</c> map that is the one piece of new Gateway state. Covers the
/// expiry predicate, the clear paths, the two bound-guards (per-Director removal and per-Director
/// live-set prune), and the persistence contract (write-through + re-arm on load + corrupt quarantine).
/// Every test uses an isolated temp path so it never touches the real snooze.json.
/// </summary>
public sealed class SnoozeRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-snooze-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "snooze.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void IsExpired_is_false_for_a_future_snooze_and_true_once_the_time_passes()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(60), "dir-1");

        Assert.True(reg.Contains("s1"));
        Assert.False(reg.IsExpired("s1", now));                 // one hour to go
        Assert.False(reg.IsExpired("s1", now.AddMinutes(59)));  // still holding
        Assert.True(reg.IsExpired("s1", now.AddMinutes(60)));   // exactly due -> expired
        Assert.True(reg.IsExpired("s1", now.AddMinutes(61)));   // past due
    }

    [Fact]
    public void IsExpired_is_false_for_a_session_with_no_entry()
    {
        var reg = new SnoozeRegistry(Path_);
        Assert.False(reg.IsExpired("nobody", DateTime.UtcNow));
        Assert.False(reg.Contains("nobody"));
    }

    [Fact]
    public void SnoozeUntilFor_returns_the_armed_deadline()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var until = now.AddHours(4);
        reg.Snooze("s1", until, "dir-1");

        Assert.Equal(until, reg.SnoozeUntilFor("s1"));
        // Returns the real deadline even once it is in the past - "is it over?" is IsExpired's ruling.
        reg.Snooze("s2", now.AddMinutes(-1), "dir-1");
        Assert.Equal(now.AddMinutes(-1), reg.SnoozeUntilFor("s2"));
    }

    [Fact]
    public void SnoozeUntilFor_is_null_for_a_deferred_snooze()
    {
        var reg = new SnoozeRegistry(Path_);
        // A deferral has no clock yet - it starts when the work ends - so there is no deadline to show.
        reg.SnoozeDeferred("s1", 720, "dir-1");

        Assert.Null(reg.SnoozeUntilFor("s1"));
    }

    [Fact]
    public void SnoozeUntilFor_is_null_for_a_session_with_no_entry()
    {
        var reg = new SnoozeRegistry(Path_);
        Assert.Null(reg.SnoozeUntilFor("nobody"));
    }

    [Fact]
    public void Snooze_again_overwrites_the_prior_time_no_escalation()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(1), "dir-1");
        Assert.True(reg.IsExpired("s1", now.AddMinutes(2)));   // first snooze would be expired

        reg.Snooze("s1", now.AddMinutes(60), "dir-1");         // re-snooze: fresh hour
        Assert.False(reg.IsExpired("s1", now.AddMinutes(2)));  // the fresh time governs
        Assert.Single(reg.Entries());                          // still one entry, not two
    }

    [Fact]
    public void Clear_removes_the_entry_and_reports_whether_there_was_one()
    {
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(60), "dir-1");

        Assert.True(reg.Clear("s1"));
        Assert.False(reg.Contains("s1"));
        Assert.False(reg.Clear("s1"));   // already gone
    }

    [Fact]
    public void ClearForDirector_drops_only_that_directors_entries()
    {
        var reg = new SnoozeRegistry(Path_);
        var until = DateTime.UtcNow.AddMinutes(60);
        reg.Snooze("a1", until, "dir-1");
        reg.Snooze("a2", until, "dir-1");
        reg.Snooze("b1", until, "dir-2");

        Assert.Equal(2, reg.ClearForDirector("dir-1"));
        Assert.False(reg.Contains("a1"));
        Assert.False(reg.Contains("a2"));
        Assert.True(reg.Contains("b1"));   // the other Director's entry survives
    }

    [Fact]
    public void PruneNotLive_drops_only_that_directors_gone_sessions()
    {
        var reg = new SnoozeRegistry(Path_);
        var until = DateTime.UtcNow.AddMinutes(60);
        reg.Snooze("live", until, "dir-1");
        reg.Snooze("exited", until, "dir-1");
        reg.Snooze("other", until, "dir-2");

        // dir-1 answered with only "live" still present; "exited" is gone. "other" belongs to dir-2 and
        // must be untouched even though it is not in dir-1's live set.
        var removed = reg.PruneNotLive("dir-1", new HashSet<string> { "live" });

        Assert.Equal(1, removed);
        Assert.True(reg.Contains("live"));
        Assert.False(reg.Contains("exited"));
        Assert.True(reg.Contains("other"));
    }

    [Fact]
    public void Entries_returned_snapshot_is_detached_from_the_live_store()
    {
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(60), "dir-1");

        var snapshot = reg.Entries();
        reg.Clear("s1");   // mutate after snapshotting

        Assert.Single(snapshot);              // the snapshot did not change under us
        Assert.False(reg.Contains("s1"));     // the live store did
    }

    [Fact]
    public void Pending_snooze_survives_a_restart_re_armed_from_disk()
    {
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var reg1 = new SnoozeRegistry(Path_);
        reg1.Snooze("s1", now.AddMinutes(60), "dir-1");

        // A fresh registry over the same file = a Gateway restart. The pending snooze must re-arm.
        var reg2 = new SnoozeRegistry(Path_);
        Assert.True(reg2.Contains("s1"));
        Assert.False(reg2.IsExpired("s1", now.AddMinutes(30)));
        Assert.True(reg2.IsExpired("s1", now.AddMinutes(90)));
    }

    [Fact]
    public void An_already_past_snooze_reloads_as_expired_so_it_fires_immediately()
    {
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var reg1 = new SnoozeRegistry(Path_);
        reg1.Snooze("s1", now.AddMinutes(-5), "dir-1"); // already past when written

        var reg2 = new SnoozeRegistry(Path_);          // restart
        Assert.True(reg2.IsExpired("s1", now));        // reads as expired on the first read -> back in needs-you (no sweep)
    }

    // ---- Defect 20: a DEFERRED entry - the snooze was asked for, the clock has not started ----

    [Fact]
    public void SnoozeDeferred_records_the_length_and_no_deadline()
    {
        // THE RULING (owner, 14 July 2026): the clock starts when the work ENDS. So a hold asked for
        // while the agent is working records what was ASKED FOR and nothing else - arming a clock at
        // request time is what let it be deleted (or expire) before the hold had even landed.
        var reg = new SnoozeRegistry(Path_);

        reg.SnoozeDeferred("s1", 720, "dir-1");

        var e = Assert.Single(reg.Entries());
        Assert.True(e.IsDeferred);
        Assert.Null(e.SnoozeUntilUtc);
        Assert.Equal(720, e.PendingMinutes);
    }

    [Fact]
    public void A_deferred_entry_is_never_expired_however_long_it_waits()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        reg.SnoozeDeferred("s1", 1, "dir-1");   // a ONE-minute snooze...

        Assert.False(reg.IsExpired("s1", now.AddYears(10)));  // ...still not expired a decade later
    }

    [Fact]
    public void Land_starts_the_clock_from_the_landing_instant()
    {
        var reg = new SnoozeRegistry(Path_);
        var landedAt = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        reg.SnoozeDeferred("s1", 720, "dir-1");

        Assert.True(reg.Land("s1", landedAt));

        var e = Assert.Single(reg.Entries());
        Assert.False(e.IsDeferred);
        Assert.Equal(landedAt.AddMinutes(720), e.SnoozeUntilUtc);  // 12 hours AFTER the work ended
        Assert.Null(e.PendingMinutes);
        Assert.False(reg.IsExpired("s1", landedAt.AddMinutes(719)));
        Assert.True(reg.IsExpired("s1", landedAt.AddMinutes(720)));
    }

    [Fact]
    public void Land_is_idempotent_and_never_restarts_a_running_clock()
    {
        // The push seam lands a deferral, and it calls Land on EVERY settled push - so a second landing on a
        // running clock must be refused. (There used to be a second caller, the sweep backstop; it is gone.)
        var reg = new SnoozeRegistry(Path_);
        var landedAt = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        reg.SnoozeDeferred("s1", 720, "dir-1");
        reg.Land("s1", landedAt);

        Assert.False(reg.Land("s1", landedAt.AddHours(5)));   // second landing: refused
        Assert.Equal(landedAt.AddMinutes(720), Assert.Single(reg.Entries()).SnoozeUntilUtc);
    }

    [Fact]
    public void Land_on_an_absent_session_is_a_no_op()
    {
        var reg = new SnoozeRegistry(Path_);
        Assert.False(reg.Land("nobody", DateTime.UtcNow));
    }

    // ---------- an elapsed entry is a DURABLE returned-by-timer tombstone (round 2 finding 2) ----------
    // There is no expiry sweep. An elapsed armed entry is NOT retired by the passage of time; it lingers,
    // reading as needs-you (HoldStateFor None) with IsExpired still true so the "Snooze ended" badge is
    // durable, until an edge that ends a snooze clears it - work, an owner turn, an exit, or a re-snooze.
    // This coverage moved here from SnoozeExpirySweepTests when the sweep was deleted; the property it
    // guards is the registry's, not a sweep's.

    [Fact]
    public void An_elapsed_armed_entry_lingers_reads_needs_you_and_keeps_the_badge_fact()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1"); // deadline already passed

        Assert.True(reg.Contains("s1"));                          // NOT retired by the passage of time
        Assert.True(reg.IsExpired("s1", now));                    // so the badge fact is durable
        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", now)); // and it reads needs-you, never held
    }

    [Fact]
    public void Work_clears_an_elapsed_tombstone()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", now));

        Assert.True(reg.ClearIfArmed("s1")); // an elapsed entry is armed (not deferred), so work removes it
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public void An_owner_turn_clears_an_elapsed_tombstone()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var baseline = now.AddMinutes(-30);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1", ownerTurnBaselineUtc: baseline);
        Assert.True(reg.IsExpired("s1", now));

        Assert.True(reg.ClearIfSupersededByOwnerTurn("s1", baseline.AddMinutes(1)));
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public void A_re_snooze_clears_an_elapsed_tombstone_and_arms_a_fresh_clock()
    {
        var reg = new SnoozeRegistry(Path_);
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", now));

        reg.Snooze("s1", now.AddHours(12), "dir-1"); // re-snooze overwrites with a future clock

        Assert.False(reg.IsExpired("s1", now));                      // badge cleared
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("s1", now));  // fresh snooze armed
    }

    [Fact]
    public void A_deferred_entry_survives_a_restart_with_its_length_intact()
    {
        var reg1 = new SnoozeRegistry(Path_);
        reg1.SnoozeDeferred("s1", 720, "dir-1");

        var reg2 = new SnoozeRegistry(Path_);   // restart
        var e = Assert.Single(reg2.Entries());
        Assert.True(e.IsDeferred);
        Assert.Equal(720, e.PendingMinutes);
    }

    [Fact]
    public void A_row_with_neither_a_deadline_nor_a_length_is_dropped_on_load()
    {
        // Such a row could never expire and never land - a snooze that would silently never return.
        // Drop it loudly rather than keep a promise that cannot be kept.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{"entries":[{"sessionId":"s1","snoozeUntilUtc":null,"directorId":"d","pendingMinutes":null}]}""");

        var reg = new SnoozeRegistry(Path_);

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_the_registry_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not valid json ");

        var reg = new SnoozeRegistry(Path_);   // must not throw - the Gateway still boots
        Assert.Empty(reg.Entries());

        var quarantined = Directory.GetFiles(_dir, "snooze.json.corrupt-*");
        Assert.Single(quarantined);            // the bad bytes were preserved, not overwritten
    }
}
