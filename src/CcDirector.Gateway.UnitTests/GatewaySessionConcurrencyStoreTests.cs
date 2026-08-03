using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the concurrency store on the statistics DATABASE: both series (live + working), the
/// all-time peak, the derived weekly max, the hourly distinct session / machine / repository log, restart
/// durability (including mid-hour dedup), hourly-history pruning, and per-tenant separation.
///
/// These are deliberately the same scenarios as <see cref="GatewaySessionConcurrencyStatsTests"/>, which
/// tests the JSON store this replaces. Output parity between the two is proved separately and directly in
/// <see cref="GatewaySessionConcurrencyParityTests"/>; these tests state the behaviour on its own terms so
/// that when the JSON store is finally deleted the specification does not go with it.
/// </summary>
public sealed class GatewaySessionConcurrencyStoreTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private GatewaySessionConcurrencyStore NewStore() => new(_db.NewFactory());

    private static readonly DateTime T0 = new(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);

    private static SessionDto S(string id, string state, string machine = "M1", string repo = "R1") =>
        new() { SessionId = id, ActivityState = state, MachineName = machine, RepoPath = repo };

    // A roster of `live` non-exited sessions, `working` of them in the Working state, on one machine/repo.
    private static List<SessionDto> Roster(int live, int working, string machine = "M1", string repo = "R1")
    {
        var list = new List<SessionDto>();
        for (var i = 0; i < working; i++) list.Add(S($"w{i}", "Working", machine, repo));
        for (var i = 0; i < live - working; i++) list.Add(S($"i{i}", "WaitingForInput", machine, repo));
        return list;
    }

    [Fact]
    public void Observe_TracksCurrentPeakAndHourly_ForBothSeries()
    {
        var s = NewStore();
        s.Observe(Roster(10, 3), T0);
        s.Observe(Roster(28, 7), T0.AddMinutes(10));
        s.Observe(Roster(20, 5), T0.AddMinutes(20)); // lower - the peak must stand

        var snap = s.Snapshot(T0.AddMinutes(21));
        Assert.Equal(20, snap.Live.Current);
        Assert.Equal(28, snap.Live.AllTimeMax);
        Assert.Equal(5, snap.Working.Current);
        Assert.Equal(7, snap.Working.AllTimeMax);
        Assert.Single(snap.Hourly);
        Assert.Equal("2026-07-11T20", snap.Hourly[0].Hour);
        Assert.Equal(28, snap.Hourly[0].MaxLive);
        Assert.Equal(7, snap.Hourly[0].MaxWorking);
    }

    [Fact]
    public void AllTimeMaxTimestamp_MovesOnlyWhenThatSeriesPeakActuallyAdvances()
    {
        var s = NewStore();
        // The live peak is set here and never beaten again; the working peak is beaten later. Each timestamp
        // must name the instant ITS OWN maximum was set, so a write that advances only one of them must not
        // drag the other's timestamp forward.
        s.Observe(Roster(28, 3), T0);
        s.Observe(Roster(10, 9), T0.AddMinutes(30));

        var snap = s.Snapshot(T0.AddHours(1));
        Assert.Equal(28, snap.Live.AllTimeMax);
        Assert.Equal(T0, snap.Live.AllTimeMaxAtUtc);
        Assert.Equal(9, snap.Working.AllTimeMax);
        Assert.Equal(T0.AddMinutes(30), snap.Working.AllTimeMaxAtUtc);
    }

    [Fact]
    public void AllTimeMaxTimestamp_StaysNull_WhileThePeakIsStillZero()
    {
        var s = NewStore();
        // An empty roster is a real observation - the hour is recorded - but nothing peaked, so there is no
        // instant to name and the store says so rather than inventing one.
        s.Observe(new List<SessionDto>(), T0);

        var snap = s.Snapshot(T0.AddMinutes(1));
        Assert.Equal(0, snap.Live.AllTimeMax);
        Assert.Null(snap.Live.AllTimeMaxAtUtc);
        Assert.Null(snap.Working.AllTimeMaxAtUtc);
        Assert.Single(snap.Hourly); // the hour itself IS recorded, with zeroes
    }

    [Fact]
    public void HourlyLog_CountsDistinctSessionsMachinesReposAcrossTheHour()
    {
        var s = NewStore();
        s.Observe(new List<SessionDto> { S("a", "Working", "M1", "R1"), S("b", "WaitingForInput", "M1", "R1") }, T0);
        s.Observe(new List<SessionDto> { S("b", "Working", "M2", "R2"), S("c", "Working", "M2", "R2") }, T0.AddMinutes(5));

        var snap = s.Snapshot(T0.AddMinutes(6));
        Assert.Single(snap.Hourly);
        Assert.Equal(3, snap.Hourly[0].Sessions); // a, b, c across the hour
        Assert.Equal(2, snap.Hourly[0].Machines); // M1, M2
        Assert.Equal(2, snap.Hourly[0].Repos);    // R1, R2
        Assert.Equal(2, snap.Hourly[0].MaxLive);
        Assert.Equal(2, snap.Hourly[0].MaxWorking);
    }

    [Fact]
    public void ExitedSessions_AreNotCounted()
    {
        var s = NewStore();
        s.Observe(new List<SessionDto> { S("a", "Working"), S("b", "Exited"), S("c", "WaitingForInput") }, T0);
        var snap = s.Snapshot(T0);
        Assert.Equal(2, snap.Live.Current);      // a + c; b is exited
        Assert.Equal(1, snap.Working.Current);   // a
        Assert.Equal(2, snap.Hourly[0].Sessions);
    }

    [Fact]
    public void WeeklyMax_IsMaxOverLast7Days_WhileAllTimeKeepsTheOlderPeak()
    {
        var s = NewStore();
        s.Observe(Roster(40, 10), T0.AddDays(-10)); // older than a week
        s.Observe(Roster(25, 6), T0.AddDays(-2));   // within the week
        s.Observe(Roster(22, 5), T0);              // within the week

        var snap = s.Snapshot(T0);
        Assert.Equal(40, snap.Live.AllTimeMax); // all-time remembers the older peak
        Assert.Equal(25, snap.Live.WeeklyMax);  // weekly only sees the last 7 days
    }

    [Fact]
    public void PeaksAndDistinctCounts_SurviveRestart()
    {
        var a = NewStore();
        a.Observe(new List<SessionDto> { S("x", "Working"), S("y", "WaitingForInput") }, T0);

        var b = NewStore(); // a restarted process: a fresh store with an empty in-memory picture
        var snap = b.Snapshot(T0.AddMinutes(1));
        Assert.Equal(2, snap.Live.AllTimeMax);
        Assert.Equal(1, snap.Working.AllTimeMax);
        Assert.Equal(2, snap.Hourly[0].Sessions);

        // A further observation in the SAME hour dedupes against the restored current-hour set.
        b.Observe(new List<SessionDto> { S("x", "Working"), S("z", "Working") }, T0.AddMinutes(2));
        var snap2 = b.Snapshot(T0.AddMinutes(3));
        Assert.Equal(3, snap2.Hourly[0].Sessions); // x, y, z - not x, y, x, z
    }

    [Fact]
    public void CurrentValues_AreNotPersisted_AndReadZeroAfterRestart()
    {
        var a = NewStore();
        a.Observe(Roster(9, 4), T0);
        Assert.Equal(9, a.Snapshot(T0).Live.Current);

        // The two current values are runtime-only by design: a restarted container has not observed the
        // fleet yet, so it must not report a "right now" number it inherited from a dead process. The peak
        // is durable; the current value is not.
        var b = NewStore();
        var snap = b.Snapshot(T0.AddMinutes(1));
        Assert.Equal(0, snap.Live.Current);
        Assert.Equal(0, snap.Working.Current);
        Assert.Equal(9, snap.Live.AllTimeMax);
    }

    [Fact]
    public void OldHourlyBuckets_ArePruned_ButAllTimePeakRemains()
    {
        var s = NewStore();
        s.Observe(Roster(12, 3), T0.AddDays(-120)); // beyond the 90-day retention
        s.Observe(Roster(15, 4), T0);              // triggers a prune at now = T0

        var snap = s.Snapshot(T0);
        Assert.Single(snap.Hourly);                 // only the recent hour survives the history
        Assert.Equal("2026-07-11T20", snap.Hourly[0].Hour);
        Assert.Equal(15, snap.Live.AllTimeMax);     // the peak is not history-derived, so it stands
    }

    [Fact]
    public void PrunedHours_TakeTheirMemberRowsWithThem()
    {
        var s = NewStore();
        s.Observe(Roster(12, 3), T0.AddDays(-120));
        using (var ctx = _db.NewFactory().CreateDbContext())
            Assert.NotEmpty(ctx.ConcurrencyHourMembers.Where(m => m.HourUtc.StartsWith("2026-03")));

        s.Observe(Roster(15, 4), T0); // prunes at now = T0

        using (var ctx = _db.NewFactory().CreateDbContext())
        {
            // The member rows are the highest-cardinality rows in the store (one per distinct session per
            // hour). An hour pruned without them would leave this the largest table in the statistics
            // database, growing forever.
            Assert.Empty(ctx.ConcurrencyHourMembers.Where(m => m.HourUtc.StartsWith("2026-03")));
            Assert.NotEmpty(ctx.ConcurrencyHourMembers.Where(m => m.HourUtc == "2026-07-11T20"));
        }
    }

    [Fact]
    public void DedupSets_KeepTheirComparers_SessionsOrdinal_MachinesAndReposIgnoringCase()
    {
        var s = NewStore();
        // One machine reported with two spellings is ONE machine, and the same for a repository path. Two
        // session ids differing only in case are TWO sessions - an id is an exact token.
        s.Observe(new List<SessionDto>
        {
            S("abc", "Working", "SOREN_NORTH", @"D:\Repos\Thing"),
            S("ABC", "Working", "Soren_North", @"d:\repos\thing"),
        }, T0);

        var snap = s.Snapshot(T0.AddMinutes(1));
        Assert.Equal(2, snap.Hourly[0].Sessions);  // "abc" and "ABC" are two sessions
        Assert.Equal(1, snap.Hourly[0].Machines);  // one machine, two spellings
        Assert.Equal(1, snap.Hourly[0].Repos);     // one repository, two spellings
    }

    [Fact]
    public void MemberTable_MayHoldTwoSpellingsOfOneMachine_AndTheCountIsStillOne()
    {
        // Two containers both already folding this hour - which is what a slot swap produces - each holding
        // its own dedup set. Container B enters the hour BEFORE container A has written anything, so B never
        // sees A's spelling and writes its own.
        var a = NewStore();
        var b = NewStore();
        b.Observe(new List<SessionDto>(), T0);

        a.Observe(new List<SessionDto> { S("s1", "Working", "SOREN_NORTH") }, T0.AddMinutes(1));
        b.Observe(new List<SessionDto> { S("s2", "Working", "Soren_North") }, T0.AddMinutes(2));

        using (var ctx = _db.NewFactory().CreateDbContext())
        {
            // Two rows for one machine. The table's key is ordinal, so both are legal, and this is HARMLESS
            // rather than a defect to normalise away: nothing ever counts or compares these rows in the
            // database.
            var machines = ctx.ConcurrencyHourMembers
                .Where(m => m.HourUtc == "2026-07-11T20" && m.Kind == ConcurrencyMemberKinds.Machine)
                .Select(m => m.MemberId).ToList();
            Assert.Equal(2, machines.Count);
        }

        // Because the count the page shows comes from the OrdinalIgnoreCase set, not from the table: both
        // containers already report one machine, and a third container starting now rehydrates both rows
        // and still reports one.
        Assert.Equal(1, a.Snapshot(T0.AddMinutes(3)).Hourly[0].Machines);
        Assert.Equal(1, b.Snapshot(T0.AddMinutes(3)).Hourly[0].Machines);

        var c = NewStore();
        c.Observe(new List<SessionDto> { S("s3", "Working", "SOREN_NORTH") }, T0.AddMinutes(4));
        Assert.Equal(1, c.Snapshot(T0.AddMinutes(5)).Hourly[0].Machines);
    }

    [Fact]
    public void AFailedWrite_KeepsTheMembersItNeverStored_Pending()
    {
        // HashSet.Add is what decides whether a member row gets written, and the fold adds the members
        // BEFORE the write. So a write that fails must put them back, or they are treated as already
        // persisted for the rest of the hour and their rows are never written at all - after which a
        // container restarting inside that hour rehydrates an incomplete set and can double-count.
        var s = NewStore();
        using (var ctx = _db.NewFactory().CreateDbContext())
            ctx.Database.ExecuteSqlRaw("ALTER TABLE concurrency_hour_member RENAME TO concurrency_hour_member_hidden");

        Assert.ThrowsAny<Exception>(() => s.Observe(new List<SessionDto> { S("a", "Working") }, T0));

        using (var ctx = _db.NewFactory().CreateDbContext())
            ctx.Database.ExecuteSqlRaw("ALTER TABLE concurrency_hour_member_hidden RENAME TO concurrency_hour_member");

        // The whole observation was one transaction, so nothing of it reached the store ...
        using (var ctx = _db.NewFactory().CreateDbContext())
        {
            Assert.Empty(ctx.ConcurrencyPeaks);
            Assert.Empty(ctx.ConcurrencyHours);
        }

        // ... and the same session observed again is still treated as unseen, so its row is written now.
        s.Observe(new List<SessionDto> { S("a", "Working") }, T0.AddMinutes(1));

        using (var ctx = _db.NewFactory().CreateDbContext())
        {
            var sessions = ctx.ConcurrencyHourMembers
                .Where(m => m.Kind == ConcurrencyMemberKinds.Session).Select(m => m.MemberId).ToList();
            Assert.Contains("a", sessions);
            Assert.Equal(1, ctx.ConcurrencyHours.Single().DistinctSessions);
        }
    }

    [Fact]
    public void UnseenTenant_ReturnsAnAllZeroSnapshotWithNoHours()
    {
        var s = NewStore();
        s.Observe(Roster(28, 7), T0, TenantA);

        var snap = s.Snapshot(T0.AddMinutes(1), TenantB);
        Assert.Equal(0, snap.Live.Current);
        Assert.Equal(0, snap.Live.AllTimeMax);
        Assert.Null(snap.Live.AllTimeMaxAtUtc);
        Assert.Equal(0, snap.Live.WeeklyMax);
        Assert.Equal(0, snap.Working.AllTimeMax);
        Assert.Empty(snap.Hourly);
    }

    [Fact]
    public void InvalidTenant_IsRefused_NeverSilentlyServedAsLocal()
    {
        var s = NewStore();
        Assert.Throws<ArgumentException>(() => s.Observe(Roster(1, 1), T0, default(TenantId)));
        Assert.Throws<ArgumentException>(() => s.Snapshot(T0, default(TenantId)));
    }

    // ---- MTR-08: two tenants' concurrency does not mix ----

    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TwoTenants_ConcurrencyAggregates_DoNotMix()
    {
        var s = NewStore();

        // Each tenant's /sessions roster is its own; the peak, current and hourly distinct counts are kept per
        // tenant, so tenant A's 28-live peak never shows up in tenant B's snapshot and vice versa.
        s.Observe(Roster(28, 7), T0, TenantA);
        s.Observe(Roster(4, 1), T0, TenantB);

        var snapA = s.Snapshot(T0.AddMinutes(1), TenantA);
        var snapB = s.Snapshot(T0.AddMinutes(1), TenantB);

        Assert.Equal(28, snapA.Live.AllTimeMax);
        Assert.Equal(7, snapA.Working.AllTimeMax);
        Assert.Equal(4, snapB.Live.AllTimeMax);
        Assert.Equal(1, snapB.Working.AllTimeMax);

        // The hourly distinct-session counts are per tenant too - A saw 28 distinct sessions that hour, B saw
        // 4, and neither is the sum.
        Assert.Equal(28, Assert.Single(snapA.Hourly).Sessions);
        Assert.Equal(4, Assert.Single(snapB.Hourly).Sessions);
    }

    [Fact]
    public void TwoTenants_Concurrency_SurvivesRestart_PerTenant()
    {
        var a = NewStore();
        a.Observe(Roster(28, 7), T0, TenantA);
        a.Observe(Roster(4, 1), T0, TenantB);

        var b = NewStore(); // a restarted process reading the same store
        Assert.Equal(28, b.Snapshot(T0.AddMinutes(1), TenantA).Live.AllTimeMax);
        Assert.Equal(4, b.Snapshot(T0.AddMinutes(1), TenantB).Live.AllTimeMax);
    }

    [Fact]
    public void Prune_IsPerTenant_AndDoesNotTouchAnotherTenantsHistory()
    {
        var s = NewStore();
        s.Observe(Roster(3, 1), T0.AddDays(-120), TenantA); // ancient, tenant A
        s.Observe(Roster(3, 1), T0.AddDays(-120), TenantB); // ancient, tenant B
        s.Observe(Roster(5, 2), T0, TenantA);               // tenant A writes at T0, so tenant A prunes

        Assert.Single(s.Snapshot(T0, TenantA).Hourly);      // A's ancient hour is gone
        Assert.Single(s.Snapshot(T0, TenantB).Hourly);      // B's ancient hour is untouched
        Assert.Equal("2026-03-13T20", s.Snapshot(T0, TenantB).Hourly[0].Hour);
    }
}
