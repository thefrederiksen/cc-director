using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Gateway-owned snooze registry (Snooze Length mission) over the EF data layer (Hosted
/// Gateway mission, Step 1b): the persisted <c>sessionId -&gt; SnoozeUntilUtc</c> map that is the one piece of
/// new Gateway state. Covers the expiry predicate, the clear paths, the two bound-guards (per-Director
/// removal and per-Director live-set prune), the armed/deferred invariant, and the persistence contract
/// (a fresh registry over the same database re-arms; the one-time JSON import and its fail-loud/drop rules).
/// </summary>
public sealed class SnoozeRegistryTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private SnoozeRegistry NewReg() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose() => _h.Dispose();

    [Fact]
    public void IsExpired_is_false_for_a_future_snooze_and_true_once_the_time_passes()
    {
        var reg = NewReg();
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
        var reg = NewReg();
        Assert.False(reg.IsExpired("nobody", DateTime.UtcNow));
        Assert.False(reg.Contains("nobody"));
    }

    [Fact]
    public void SnoozeUntilFor_returns_the_armed_deadline()
    {
        var reg = NewReg();
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
        var reg = NewReg();
        // A deferral has no clock yet - it starts when the work ends - so there is no deadline to show.
        reg.SnoozeDeferred("s1", 720, "dir-1");

        Assert.Null(reg.SnoozeUntilFor("s1"));
    }

    [Fact]
    public void SnoozeUntilFor_is_null_for_a_session_with_no_entry()
    {
        var reg = NewReg();
        Assert.Null(reg.SnoozeUntilFor("nobody"));
    }

    [Fact]
    public void Snooze_again_overwrites_the_prior_time_no_escalation()
    {
        var reg = NewReg();
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
        var reg = NewReg();
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(60), "dir-1");

        Assert.True(reg.Clear("s1"));
        Assert.False(reg.Contains("s1"));
        Assert.False(reg.Clear("s1"));   // already gone
    }

    [Fact]
    public void ClearForDirector_drops_only_that_directors_entries()
    {
        var reg = NewReg();
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
        var reg = NewReg();
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
        var reg = NewReg();
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
        var reg1 = NewReg();
        reg1.Snooze("s1", now.AddMinutes(60), "dir-1");

        // A fresh registry over the same file = a Gateway restart. The pending snooze must re-arm.
        var reg2 = NewReg();
        Assert.True(reg2.Contains("s1"));
        Assert.False(reg2.IsExpired("s1", now.AddMinutes(30)));
        Assert.True(reg2.IsExpired("s1", now.AddMinutes(90)));
    }

    [Fact]
    public void An_already_past_snooze_reloads_as_expired_so_it_fires_immediately()
    {
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var reg1 = NewReg();
        reg1.Snooze("s1", now.AddMinutes(-5), "dir-1"); // already past when written

        var reg2 = NewReg();          // restart
        Assert.True(reg2.IsExpired("s1", now));        // reads as expired on the first read -> back in needs-you (no sweep)
    }

    // ---- Defect 20: a DEFERRED entry - the snooze was asked for, the clock has not started ----

    [Fact]
    public void SnoozeDeferred_records_the_length_and_no_deadline()
    {
        // THE RULING (owner, 14 July 2026): the clock starts when the work ENDS. So a hold asked for
        // while the agent is working records what was ASKED FOR and nothing else - arming a clock at
        // request time is what let it be deleted (or expire) before the hold had even landed.
        var reg = NewReg();

        reg.SnoozeDeferred("s1", 720, "dir-1");

        var e = Assert.Single(reg.Entries());
        Assert.True(e.IsDeferred);
        Assert.Null(e.SnoozeUntilUtc);
        Assert.Equal(720, e.PendingMinutes);
    }

    [Fact]
    public void A_deferred_entry_is_never_expired_however_long_it_waits()
    {
        var reg = NewReg();
        var now = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        reg.SnoozeDeferred("s1", 1, "dir-1");   // a ONE-minute snooze...

        Assert.False(reg.IsExpired("s1", now.AddYears(10)));  // ...still not expired a decade later
    }

    [Fact]
    public void Land_starts_the_clock_from_the_landing_instant()
    {
        var reg = NewReg();
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
        var reg = NewReg();
        var landedAt = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        reg.SnoozeDeferred("s1", 720, "dir-1");
        reg.Land("s1", landedAt);

        Assert.False(reg.Land("s1", landedAt.AddHours(5)));   // second landing: refused
        Assert.Equal(landedAt.AddMinutes(720), Assert.Single(reg.Entries()).SnoozeUntilUtc);
    }

    [Fact]
    public void Land_on_an_absent_session_is_a_no_op()
    {
        var reg = NewReg();
        Assert.False(reg.Land("nobody", DateTime.UtcNow));
    }

    [Fact]
    public void A_deferred_entry_survives_a_restart_with_its_length_intact()
    {
        var reg1 = NewReg();
        reg1.SnoozeDeferred("s1", 720, "dir-1");

        var reg2 = NewReg();   // restart
        var e = Assert.Single(reg2.Entries());
        Assert.True(e.IsDeferred);
        Assert.Equal(720, e.PendingMinutes);
    }

    [Fact]
    public void Import_DropsARowWithNeitherADeadlineNorALength()
    {
        // Such a row could never expire and never land - a snooze that would silently never return. The
        // one-time import drops it loudly (mirroring the old load) rather than keep a promise it cannot keep.
        var legacy = LegacyFile();
        File.WriteAllText(legacy, """{"entries":[{"sessionId":"s1","snoozeUntilUtc":null,"directorId":"d","pendingMinutes":null}]}""");

        var reg = new SnoozeRegistry(Db, legacy);

        Assert.Empty(reg.Entries());
    }

    [Fact]
    public void Import_CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyFile();
        const string corrupt = "{ this is not valid json ";
        File.WriteAllText(legacy, corrupt);

        // Fail-loud, no partial import, no silent quarantine (the EF data-layer contract).
        Assert.Throws<InvalidOperationException>(() => new SnoozeRegistry(Db, legacy));
        Assert.True(File.Exists(legacy));
        Assert.Equal(corrupt, File.ReadAllText(legacy));
    }

    [Fact]
    public void LegacyJson_ImportedOnce_ArmedAndDeferred_InvariantPreserved_ThenRenamedAside()
    {
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var baseline = new DateTime(2026, 7, 16, 11, 0, 0, DateTimeKind.Utc);
        var legacy = LegacyFile();
        // An ARMED entry (a deadline, no pending) and a DEFERRED entry (a length, no deadline), plus the
        // owner-turn baseline - exactly the two shapes the old store wrote.
        File.WriteAllText(legacy, JsonSerializer.Serialize(new
        {
            entries = new object[]
            {
                new { sessionId = "armed-1", snoozeUntilUtc = (DateTime?)now.AddHours(4), directorId = "dir-1", pendingMinutes = (int?)null, ownerTurnBaselineUtc = (DateTime?)baseline },
                new { sessionId = "deferred-1", snoozeUntilUtc = (DateTime?)null, directorId = "dir-2", pendingMinutes = (int?)720, ownerTurnBaselineUtc = (DateTime?)null },
            },
        }));

        var reg = new SnoozeRegistry(Db, legacy);

        var armed = reg.Entries().Single(e => e.SessionId == "armed-1");
        Assert.False(armed.IsDeferred);
        Assert.Equal(now.AddHours(4), armed.SnoozeUntilUtc);
        Assert.Equal(DateTimeKind.Utc, armed.SnoozeUntilUtc!.Value.Kind);
        Assert.Null(armed.PendingMinutes);
        Assert.Equal("dir-1", armed.DirectorId);
        Assert.Equal(baseline, armed.OwnerTurnBaselineUtc);

        var deferred = reg.Entries().Single(e => e.SessionId == "deferred-1");
        Assert.True(deferred.IsDeferred);
        Assert.Null(deferred.SnoozeUntilUtc);
        Assert.Equal(720, deferred.PendingMinutes);
        Assert.Equal("dir-2", deferred.DirectorId);
        Assert.Null(deferred.OwnerTurnBaselineUtc);

        // The armed/deferred invariant is reflected in the hold state for both.
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("armed-1", now));
        Assert.Equal(HoldStates.DeferredHold, reg.HoldStateFor("deferred-1", now));

        // Renamed aside (kept as a backup); a fresh registry does not re-import.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));
        Assert.Equal(2, new SnoozeRegistry(Db, legacy).Entries().Count);
    }

    [Fact]
    public void Import_NullRoot_FailsLoud_AndLeavesTheFileInPlace()
    {
        // The JSON literal "null" is an unreadable store, not an empty one: fail loud and leave the file in
        // place, rather than committing zero rows and renaming an invalid store aside as if it had migrated.
        var legacy = LegacyFile();
        File.WriteAllText(legacy, "null");

        Assert.Throws<InvalidOperationException>(() => new SnoozeRegistry(Db, legacy));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void Import_NullEntriesList_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyFile();
        File.WriteAllText(legacy, """{"entries":null}""");

        Assert.Throws<InvalidOperationException>(() => new SnoozeRegistry(Db, legacy));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void Import_RetainsANullDirectorId_ExactlyAsTheLegacyStoreHeld()
    {
        // The old store retained a null DirectorId exactly as read (it never coerced it to ""), so the import
        // must too - a null stays null on round-trip through DirectorIdFor and Entries.
        var legacy = LegacyFile();
        File.WriteAllText(legacy, """{"entries":[{"sessionId":"s1","snoozeUntilUtc":"2026-07-16T16:00:00Z","directorId":null,"pendingMinutes":null}]}""");

        var reg = new SnoozeRegistry(Db, legacy);

        Assert.Null(reg.DirectorIdFor("s1"));
        Assert.Null(Assert.Single(reg.Entries()).DirectorId);
    }

    [Fact]
    public void Entries_IsOrderedBySessionId_Deterministic()
    {
        // The old Dictionary enumeration order was undefined; Entries() now returns a stable session-id order.
        var reg = NewReg();
        var until = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s-charlie", until, "d");
        reg.Snooze("s-alpha", until, "d");
        reg.Snooze("s-bravo", until, "d");

        Assert.Equal(new[] { "s-alpha", "s-bravo", "s-charlie" }, reg.Entries().Select(e => e.SessionId).ToArray());
    }

    // ---- ClearIfArmed and the durable returned-by-timer tombstone (round 2 finding 2) ----
    // There is no expiry sweep. ClearIfArmed is the working edge's delete: it removes an ARMED entry
    // (running or elapsed) and spares a DEFERRED one. An elapsed armed entry is NOT retired by the passage
    // of time; it lingers, reading as needs-you (HoldStateFor None) with IsExpired still true so the
    // "Snooze ended" badge is durable, until an edge that ends a snooze clears it - work (ClearIfArmed), an
    // owner turn, an exit, or a re-snooze. This coverage moved here from SnoozeExpirySweepTests when the
    // sweep was deleted; the property it guards is the registry's, not a sweep's.

    [Fact]
    public void ClearIfArmed_deletes_an_armed_entry_and_spares_a_deferred_one()
    {
        var reg = NewReg();
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("armed", now.AddHours(12), "dir-1");   // armed (running)
        reg.SnoozeDeferred("deferred", 720, "dir-1");      // deferred - no deadline yet

        Assert.True(reg.ClearIfArmed("armed"));            // armed -> deleted
        Assert.False(reg.Contains("armed"));
        Assert.False(reg.ClearIfArmed("deferred"));        // deferred -> spared (only Land converts it)
        Assert.True(reg.Contains("deferred"));
        Assert.False(reg.ClearIfArmed("nobody"));          // no entry -> false
    }

    [Fact]
    public void An_elapsed_armed_entry_lingers_reads_needs_you_and_keeps_the_badge_fact()
    {
        var reg = NewReg();
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1"); // deadline already passed

        Assert.True(reg.Contains("s1"));                            // NOT retired by the passage of time
        Assert.True(reg.IsExpired("s1", now));                      // so the badge fact is durable
        Assert.Equal(HoldStates.None, reg.HoldStateFor("s1", now)); // and it reads needs-you, never held
    }

    [Fact]
    public void Work_clears_an_elapsed_tombstone()
    {
        var reg = NewReg();
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", now));

        Assert.True(reg.ClearIfArmed("s1")); // an elapsed entry is armed (not deferred), so work removes it
        Assert.False(reg.Contains("s1"));
    }

    [Fact]
    public void An_owner_turn_clears_an_elapsed_tombstone()
    {
        var reg = NewReg();
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
        var reg = NewReg();
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("s1", now.AddMinutes(-1), "dir-1");
        Assert.True(reg.IsExpired("s1", now));

        reg.Snooze("s1", now.AddHours(12), "dir-1"); // re-snooze overwrites with a future clock

        Assert.False(reg.IsExpired("s1", now));                      // badge cleared
        Assert.Equal(HoldStates.Held, reg.HoldStateFor("s1", now));  // fresh snooze armed
    }

    private string LegacyFile()
    {
        var legacy = _h.LegacyPath("snooze-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        return legacy;
    }
}
