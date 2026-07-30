using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// OUTPUT PARITY between the JSON store (<see cref="GatewaySessionConcurrencyStats"/>, which writes
/// <c>gateway-concurrency-stats.json</c> to the shared file share) and the database store
/// (<see cref="GatewaySessionConcurrencyStore"/>, which replaces it).
///
/// One fixture is driven through BOTH implementations, observation for observation, and what is compared is
/// the RENDERED snapshot - the exact JSON body the <c>/stats/data</c> route serves for its
/// <c>concurrency</c> property, serialized with the same web defaults ASP.NET Core minimal APIs use. That is
/// deliberate and it is not the same claim as "the same numbers are stored": storing equal numbers and
/// rendering an equal page are two different properties, and only the second is what the owner sees. A
/// difference in a null timestamp, a DateTime Kind, the order of the hourly list, or an hour bucket that one
/// store creates and the other does not, all show up here and none of them would show up in a row-by-row
/// comparison of the two stores' contents.
///
/// SCOPE, stated rather than implied. The fixture moves forward in time, because production time does. If an
/// observation ever arrived for an hour EARLIER than one already folded, the two implementations would
/// diverge - the JSON store cleared its dedup sets and started that hour's distinct counts again from
/// nothing, whereas the database store rehydrates that hour's members and carries on counting. The database
/// store is the more accurate of the two there; it is called out because this test does not cover it.
/// </summary>
public sealed class GatewaySessionConcurrencyParityTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();
    private readonly string _jsonPath =
        Path.Combine(Path.GetTempPath(), "cc-conc-parity-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_jsonPath); } catch (IOException) { /* temp artifact */ }
    }

    private static readonly DateTime T0 = new(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    private static readonly TenantId TenantNeverSeen = new("33333333-3333-3333-3333-333333333333");

    // The serializer the /stats/data route effectively uses: minimal APIs serialize with the web defaults
    // (camelCase names, ISO-8601 instants). Rendering through it is what makes this a page comparison rather
    // than an object comparison.
    private static readonly JsonSerializerOptions RenderOptions = new(JsonSerializerDefaults.Web);

    private static string Render(ConcurrencySnapshot snapshot) => JsonSerializer.Serialize(snapshot, RenderOptions);

    private static SessionDto S(string id, string state, string machine = "M1", string repo = "R1") =>
        new() { SessionId = id, ActivityState = state, MachineName = machine, RepoPath = repo };

    private static List<SessionDto> Roster(int live, int working, string machine = "M1", string repo = "R1")
    {
        var list = new List<SessionDto>();
        for (var i = 0; i < working; i++) list.Add(S($"w{i}", "Working", machine, repo));
        for (var i = 0; i < live - working; i++) list.Add(S($"i{i}", "WaitingForInput", machine, repo));
        return list;
    }

    /// <summary>
    /// The fixture, applied identically to both implementations. It is built to touch every part of the
    /// snapshot that could differ: two tenants, hour and day rolls, a peak that is beaten and then not, the
    /// seven-day weekly window boundary, the ninety-day retention boundary, an empty roster, exited
    /// sessions, sessions with no machine or repository, and one machine reported under two spellings.
    /// </summary>
    private static void DriveFixture(Action<IReadOnlyCollection<SessionDto>, DateTime, TenantId?> observe)
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

    private void AssertRenderedSnapshotsMatch(GatewaySessionConcurrencyStats json, GatewaySessionConcurrencyStore db,
        DateTime at, TenantId? tenant, string because)
    {
        var expected = Render(json.Snapshot(at, tenant));
        var actual = Render(db.Snapshot(at, tenant));
        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(because)); // the reason is documentation, not decoration
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_AcrossTheWholeFixture()
    {
        var json = new GatewaySessionConcurrencyStats(_jsonPath);
        var db = new GatewaySessionConcurrencyStore(_db.NewFactory());

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
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_AfterBothStoresRestart()
    {
        var json = new GatewaySessionConcurrencyStats(_jsonPath);
        var db = new GatewaySessionConcurrencyStore(_db.NewFactory());
        DriveFixture((roster, at, tenant) =>
        {
            json.Observe(roster, at, tenant);
            db.Observe(roster, at, tenant);
        });

        // Restart both: the JSON store reloads its file, the database store starts with an empty in-memory
        // picture and reads the tables. Both must lose the two current values and keep everything else.
        var jsonAfter = new GatewaySessionConcurrencyStats(_jsonPath);
        var dbAfter = new GatewaySessionConcurrencyStore(_db.NewFactory());

        var readAt = T0.AddHours(1).AddMinutes(11);
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

    [Fact]
    public void RenderedSnapshot_IsIdentical_OnTheRetentionBoundary()
    {
        // The retention edge is worth its own case: the file store dropped an hour bucket when the START of
        // its hour was before the cutoff INSTANT, and the database store prunes with a text range on the hour
        // key. Getting the boundary off by one keeps or drops exactly one hour, which is a visible row on the
        // chart and would not show up anywhere else.
        var json = new GatewaySessionConcurrencyStats(_jsonPath);
        var db = new GatewaySessionConcurrencyStore(_db.NewFactory());

        var now = new DateTime(2026, 7, 11, 20, 30, 0, DateTimeKind.Utc);
        foreach (var at in new[]
                 {
                     now.AddDays(-90).AddHours(-1), // comfortably stale
                     now.AddDays(-90),              // exactly ninety days back, mid-hour: stale
                     now.AddDays(-90).AddMinutes(31), // the hour the cutoff falls inside, later in it
                     now.AddDays(-89),              // inside the window
                 })
        {
            json.Observe(Roster(3, 1), at);
            db.Observe(Roster(3, 1), at);
        }

        json.Observe(Roster(5, 2), now);
        db.Observe(Roster(5, 2), now);

        AssertRenderedSnapshotsMatch(json, db, now, null, "both stores must keep exactly the same hour buckets at the cutoff");
    }
}
