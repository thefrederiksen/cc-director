using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The concurrency store's two load-bearing proofs - the lost-update race and output parity with the JSON
/// store it replaces - written ONCE and run against BOTH providers.
///
/// Written once on purpose. A separate Postgres copy of these scenarios would be a second implementation of
/// the proof, and two implementations that agree prove only that two people made the same assumptions. What
/// carries weight is the SAME assertion, on the same fixture, run through SQLite (what self-host keeps) and
/// through real PostgreSQL (what the hosted Gateway runs). Anything the two providers do differently -
/// GREATEST versus MAX, schema qualification, how a timestamp parameter is typed, how text sorts - shows up
/// as a difference in one of these results and nowhere else.
/// </summary>
internal static class ConcurrencyStoreScenarios
{
    public static readonly DateTime T0 = new(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);
    public const string RaceHourKey = "2026-07-11T20";

    public static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    public static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    public static readonly TenantId TenantNeverSeen = new("33333333-3333-3333-3333-333333333333");

    // The serializer the /stats/data route effectively uses: minimal APIs serialize with the web defaults
    // (camelCase names, ISO-8601 instants). Rendering through it is what makes the parity check a comparison
    // of the PAGE rather than of two objects.
    private static readonly JsonSerializerOptions RenderOptions = new(JsonSerializerDefaults.Web);

    public static string Render(ConcurrencySnapshot snapshot) => JsonSerializer.Serialize(snapshot, RenderOptions);

    public static SessionDto S(string id, string state, string machine = "M1", string repo = "R1") =>
        new() { SessionId = id, ActivityState = state, MachineName = machine, RepoPath = repo };

    public static List<SessionDto> Roster(int live, int working, string machine = "M1", string repo = "R1")
    {
        var list = new List<SessionDto>();
        for (var i = 0; i < working; i++) list.Add(S($"w{i}", "Working", machine, repo));
        for (var i = 0; i < live - working; i++) list.Add(S($"i{i}", "WaitingForInput", machine, repo));
        return list;
    }

    // ---- the lost-update race ----

    /// <summary>One Gateway CONTAINER, in the two halves the race needs: it decides what to write from the
    /// picture it holds now, and it writes later.</summary>
    public interface IContainer
    {
        void Prepare(int live, DateTime at);
        void Commit();
    }

    /// <summary>The real store. Prepare only remembers the roster; the decision and the write happen together
    /// inside Observe, and the write is the upsert.</summary>
    public sealed class UpsertContainer : IContainer
    {
        private readonly GatewaySessionConcurrencyStore _store;
        private readonly TenantId _tenant;
        private List<SessionDto> _roster = new();
        private DateTime _at;

        public UpsertContainer(IDbContextFactory<GatewayStatsDbContext> factory, TenantId tenant)
        {
            _store = new GatewaySessionConcurrencyStore(factory);
            _tenant = tenant;
        }

        public void Prepare(int live, DateTime at)
        {
            _roster = Roster(live, live);
            _at = at;
        }

        public void Commit() => _store.Observe(_roster, _at, _tenant);
    }

    /// <summary>
    /// DELIBERATELY WRONG, and kept wrong on purpose. This is the naive port: read the row through the change
    /// tracker, work out the new maximum in memory, save. It is what the store would look like if the ruling
    /// on upserts had been ignored, and it exists so the assertion in <see cref="RunTheRace"/> has something
    /// to fail against. Nothing in the product references it.
    /// </summary>
    public sealed class ReadModifyWriteContainer : IContainer, IDisposable
    {
        private readonly IDbContextFactory<GatewayStatsDbContext> _factory;
        private readonly TenantId _tenant;
        private GatewayStatsDbContext? _ctx;

        public ReadModifyWriteContainer(IDbContextFactory<GatewayStatsDbContext> factory, TenantId tenant)
        {
            _factory = factory;
            _tenant = tenant;
        }

        public void Prepare(int live, DateTime at)
        {
            _ctx = _factory.CreateDbContext();
            var tenantValue = _tenant.Value;

            var peak = _ctx.ConcurrencyPeaks.FirstOrDefault(p => p.Tenant == tenantValue);
            if (peak is null)
            {
                _ctx.ConcurrencyPeaks.Add(new ConcurrencyPeakEntity
                {
                    Tenant = tenantValue,
                    LiveMax = live,
                    LiveMaxAtUtc = live > 0 ? at : null,
                });
            }
            else if (live > peak.LiveMax)
            {
                peak.LiveMax = live;
                peak.LiveMaxAtUtc = at;
            }

            var hour = _ctx.ConcurrencyHours.FirstOrDefault(h => h.Tenant == tenantValue && h.HourUtc == RaceHourKey);
            if (hour is null)
            {
                _ctx.ConcurrencyHours.Add(new ConcurrencyHourEntity
                {
                    Tenant = tenantValue,
                    HourUtc = RaceHourKey,
                    MaxLive = live,
                });
            }
            else if (live > hour.MaxLive)
            {
                hour.MaxLive = live;
            }
        }

        public void Commit()
        {
            if (_ctx is null) throw new InvalidOperationException("Commit without Prepare.");
            _ctx.SaveChanges();
            _ctx.Dispose();
            _ctx = null;
        }

        public void Dispose() => _ctx?.Dispose();
    }

    /// <summary>
    /// Two containers, one store, one hour bucket and one all-time peak.
    ///
    /// Both have already seen the fleet at five, so both hold a picture that says five. The fleet then grows,
    /// and they observe it at different moments: container A sees eight, container B sees seven. Their writes
    /// interleave - each decides what to write BEFORE the other has written - and B's write lands last.
    ///
    /// The maximum ever seen was eight, so eight is what the store must hold. A writer that computes the
    /// maximum in its own memory and then writes an absolute value overwrites A's eight with B's seven, and
    /// the owner's dashboard quietly loses the peak it is there to report.
    /// </summary>
    public static void RunTheRace(
        Func<IDbContextFactory<GatewayStatsDbContext>> newContainerFactory,
        Func<IDbContextFactory<GatewayStatsDbContext>, TenantId, IContainer> container,
        TenantId tenant)
    {
        var a = container(newContainerFactory(), tenant);
        var b = container(newContainerFactory(), tenant);

        // Seed: sequentially, so both containers and the store agree the maximum is five.
        a.Prepare(5, T0); a.Commit();
        b.Prepare(5, T0.AddMinutes(1)); b.Commit();

        // The race.
        a.Prepare(8, T0.AddMinutes(2));
        b.Prepare(7, T0.AddMinutes(3));
        a.Commit();
        b.Commit();

        var tenantValue = tenant.Value;
        using var ctx = newContainerFactory().CreateDbContext();
        var peak = ctx.ConcurrencyPeaks.AsNoTracking().Single(p => p.Tenant == tenantValue);
        var hour = ctx.ConcurrencyHours.AsNoTracking().Single(h => h.Tenant == tenantValue && h.HourUtc == RaceHourKey);

        Assert.Equal(8, peak.LiveMax);
        Assert.Equal(8, hour.MaxLive);
        // The timestamp belongs to the write that actually set the maximum, so it must be A's instant and
        // not B's - a peak stamped with the moment of the write that did NOT set it is a wrong answer that
        // looks right.
        Assert.Equal(T0.AddMinutes(2), peak.LiveMaxAtUtc);

        (a as IDisposable)?.Dispose();
        (b as IDisposable)?.Dispose();
    }

    // ---- output parity with the JSON store ----

    /// <summary>
    /// The fixture, applied identically to both implementations. It is built to touch every part of the
    /// snapshot that could differ: two tenants, hour and day rolls, a peak that is beaten and then not, the
    /// seven-day weekly window boundary, the ninety-day retention boundary, an empty roster, exited
    /// sessions, sessions with no machine or repository, and one machine reported under two spellings.
    /// </summary>
    public static void DriveFixture(Action<IReadOnlyCollection<SessionDto>, DateTime, TenantId?> observe)
    {
        // Beyond the retention window - must be pruned out of both stores by the time anything is read.
        observe(Roster(31, 11), T0.AddDays(-120), TenantA);

        // Older than a week: contributes to the all-time peak but not to the weekly maximum.
        observe(Roster(40, 12), T0.AddDays(-10), TenantA);
        observe(Roster(6, 2), T0.AddDays(-10), TenantB);

        // Inside the week.
        observe(Roster(25, 6), T0.AddDays(-2), TenantA);
        observe(Roster(9, 3), T0.AddDays(-2).AddHours(1), TenantA);

        // The current hour, several observations, including one that does not beat the peak.
        observe(Roster(10, 3), T0, TenantA);
        observe(Roster(28, 7), T0.AddMinutes(10), TenantA);
        observe(Roster(20, 5), T0.AddMinutes(20), TenantA);

        // Exited sessions are not live; a session with no machine or repository still counts as a session.
        observe(new List<SessionDto>
        {
            S("gone", "Exited"),
            S("here", "Working"),
            new() { SessionId = "bare", ActivityState = "WaitingForInput", MachineName = "", RepoPath = "" },
        }, T0.AddMinutes(30), TenantA);

        // One machine and one repository under two spellings: one machine, one repository, two sessions.
        observe(new List<SessionDto>
        {
            S("case1", "Working", "SOREN_NORTH", @"D:\Repos\Thing"),
            S("case2", "Working", "Soren_North", @"d:\repos\thing"),
        }, T0.AddMinutes(40), TenantA);

        // An empty roster is a real observation: the hour exists, nothing peaks.
        observe(new List<SessionDto>(), T0.AddHours(1), TenantB);

        // The last observation for each tenant sets the runtime-only current values.
        observe(Roster(4, 1), T0.AddHours(1).AddMinutes(5), TenantB);
        observe(Roster(13, 4), T0.AddHours(1).AddMinutes(10), TenantA);
    }

    public static void AssertRenderedSnapshotsMatch(GatewaySessionConcurrencyStats json,
        GatewaySessionConcurrencyStore db, DateTime at, TenantId? tenant, string because)
    {
        Assert.False(string.IsNullOrWhiteSpace(because)); // the reason is documentation, not decoration
        Assert.Equal(Render(json.Snapshot(at, tenant)), Render(db.Snapshot(at, tenant)));
    }

    /// <summary>Drive the whole fixture through both implementations and compare the RENDERED snapshot at
    /// several instants and for several tenants, then restart both and compare again.</summary>
    public static void AssertOutputParityAcrossTheFixture(
        Func<IDbContextFactory<GatewayStatsDbContext>> newContainerFactory, string jsonPath)
    {
        var json = new GatewaySessionConcurrencyStats(jsonPath);
        var db = new GatewaySessionConcurrencyStore(newContainerFactory());

        DriveFixture((roster, at, tenant) =>
        {
            json.Observe(roster, at, tenant);
            db.Observe(roster, at, tenant);
        });

        var readAt = T0.AddHours(1).AddMinutes(11);
        AssertRenderedSnapshotsMatch(json, db, readAt, TenantA, "tenant A drove most of the fixture");
        AssertRenderedSnapshotsMatch(json, db, readAt, TenantB, "tenant B must not have inherited any of A's numbers");
        AssertRenderedSnapshotsMatch(json, db, readAt, TenantNeverSeen, "an unseen tenant renders all zeroes and no hours");
        AssertRenderedSnapshotsMatch(json, db, readAt, null, "the default (self-host Local) tenant has never been observed here");

        // A read at a later instant moves the weekly window, so the derived weekly maximum is recomputed from
        // the hourly log rather than stored. Both must recompute it the same way.
        AssertRenderedSnapshotsMatch(json, db, T0.AddDays(6), TenantA, "the weekly window has moved past part of the log");
        AssertRenderedSnapshotsMatch(json, db, T0.AddDays(30), TenantA, "the weekly window no longer covers any hour in the log");

        // Restart both: the JSON store reloads its file, the database store starts with an empty in-memory
        // picture and reads the tables. Both must lose the two current values and keep everything else.
        var jsonAfter = new GatewaySessionConcurrencyStats(jsonPath);
        var dbAfter = new GatewaySessionConcurrencyStore(newContainerFactory());
        AssertRenderedSnapshotsMatch(jsonAfter, dbAfter, readAt, TenantA, "peaks and the hourly log are durable, current values are not");
        AssertRenderedSnapshotsMatch(jsonAfter, dbAfter, readAt, TenantB, "and the same for the second tenant");
        Assert.Equal(0, dbAfter.Snapshot(readAt, TenantA).Live.Current);

        // And a further observation in the SAME hour dedupes against the restored current-hour sets in both,
        // which is the property the member table exists for.
        var more = new List<SessionDto> { S("w0", "Working"), S("brand-new", "Working") };
        var at = T0.AddHours(1).AddMinutes(20);
        jsonAfter.Observe(more, at, TenantA);
        dbAfter.Observe(more, at, TenantA);
        AssertRenderedSnapshotsMatch(jsonAfter, dbAfter, at, TenantA, "a restored dedup set must not double-count a session it already saw");
    }

    /// <summary>
    /// VALIDATE THE DETECTOR. The parity check passes; this is what says the passing means something.
    ///
    /// A comparison of two rendered snapshots would also pass if the renderer returned a constant, if both
    /// stores were fed nothing, or if some later change quietly stopped the fixture reaching either store.
    /// So here the two stores are deliberately driven APART by a single extra observation given to only one
    /// of them, and the comparison must NOTICE. A detector that has never been shown to fire is not evidence
    /// that the thing it watches for is absent.
    /// </summary>
    public static void AssertTheParityComparisonDetectsADifference(
        Func<IDbContextFactory<GatewayStatsDbContext>> newContainerFactory, string jsonPath, TenantId tenant)
    {
        var json = new GatewaySessionConcurrencyStats(jsonPath);
        var db = new GatewaySessionConcurrencyStore(newContainerFactory());

        json.Observe(Roster(5, 2), T0, tenant);
        db.Observe(Roster(5, 2), T0, tenant);
        Assert.Equal(Render(json.Snapshot(T0, tenant)), Render(db.Snapshot(T0, tenant)));

        // One observation more, to one store only - a single session appearing on one side and not the
        // other, which is the smallest divergence this port could produce.
        db.Observe(Roster(6, 2), T0.AddMinutes(5), tenant);
        Assert.NotEqual(Render(json.Snapshot(T0.AddMinutes(6), tenant)), Render(db.Snapshot(T0.AddMinutes(6), tenant)));
    }

    /// <summary>
    /// The retention edge, which deserves its own case: the file store dropped an hour bucket when the START
    /// of its hour was before the cutoff INSTANT, and the database store prunes with a text range on the hour
    /// key. Being off by one keeps or drops exactly one hour, which is a visible row on the chart and would
    /// show up nowhere else.
    /// </summary>
    public static void AssertOutputParityOnTheRetentionBoundary(
        Func<IDbContextFactory<GatewayStatsDbContext>> newContainerFactory, string jsonPath, TenantId tenant)
    {
        var json = new GatewaySessionConcurrencyStats(jsonPath);
        var db = new GatewaySessionConcurrencyStore(newContainerFactory());

        var now = new DateTime(2026, 7, 11, 20, 30, 0, DateTimeKind.Utc);
        foreach (var at in new[]
                 {
                     now.AddDays(-90).AddHours(-1),   // comfortably stale
                     now.AddDays(-90),                // exactly ninety days back, mid-hour: stale
                     now.AddDays(-90).AddMinutes(31), // the hour the cutoff falls inside, later in it
                     now.AddDays(-89),                // inside the window
                 })
        {
            json.Observe(Roster(3, 1), at, tenant);
            db.Observe(Roster(3, 1), at, tenant);
        }

        json.Observe(Roster(5, 2), now, tenant);
        db.Observe(Roster(5, 2), now, tenant);

        AssertRenderedSnapshotsMatch(json, db, now, tenant, "both stores must keep exactly the same hour buckets at the cutoff");
    }
}
