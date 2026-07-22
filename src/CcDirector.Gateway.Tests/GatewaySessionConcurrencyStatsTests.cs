using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the DevThrottle Stats fleet-concurrency tracker: both series (live + working), the
/// all-time peak, the derived weekly max, the hourly distinct session / machine / repository log, restart
/// durability (including mid-hour dedup), and hourly-history pruning.
/// </summary>
public sealed class GatewaySessionConcurrencyStatsTests : IDisposable
{
    private readonly string _path;

    public GatewaySessionConcurrencyStatsTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "cc-conc-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (Exception) { /* best effort */ }
    }

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
        var s = new GatewaySessionConcurrencyStats(_path);
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
    public void HourlyLog_CountsDistinctSessionsMachinesReposAcrossTheHour()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
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
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(new List<SessionDto> { S("a", "Working"), S("b", "Exited"), S("c", "WaitingForInput") }, T0);
        var snap = s.Snapshot(T0);
        Assert.Equal(2, snap.Live.Current);      // a + c; b is exited
        Assert.Equal(1, snap.Working.Current);   // a
        Assert.Equal(2, snap.Hourly[0].Sessions);
    }

    [Fact]
    public void WeeklyMax_IsMaxOverLast7Days_WhileAllTimeKeepsTheOlderPeak()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
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
        var a = new GatewaySessionConcurrencyStats(_path);
        a.Observe(new List<SessionDto> { S("x", "Working"), S("y", "WaitingForInput") }, T0);

        var b = new GatewaySessionConcurrencyStats(_path); // reload from disk
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
    public void OldHourlyBuckets_ArePruned_ButAllTimePeakRemains()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(Roster(12, 3), T0.AddDays(-120)); // beyond the 90-day retention
        s.Observe(Roster(15, 4), T0);              // triggers a prune at now = T0

        var snap = s.Snapshot(T0);
        Assert.Single(snap.Hourly);                 // only the recent hour survives the history
        Assert.Equal("2026-07-11T20", snap.Hourly[0].Hour);
        Assert.Equal(15, snap.Live.AllTimeMax);     // the peak is not history-derived, so it stands
    }

    // ---- MTR-08: two tenants' concurrency does not mix ----

    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TwoTenants_ConcurrencyAggregates_DoNotMix()
    {
        var s = new GatewaySessionConcurrencyStats(_path);

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
        var a = new GatewaySessionConcurrencyStats(_path);
        a.Observe(Roster(28, 7), T0, TenantA);
        a.Observe(Roster(4, 1), T0, TenantB);

        var b = new GatewaySessionConcurrencyStats(_path); // reload the version-2 per-tenant store from disk
        Assert.Equal(28, b.Snapshot(T0.AddMinutes(1), TenantA).Live.AllTimeMax);
        Assert.Equal(4, b.Snapshot(T0.AddMinutes(1), TenantB).Live.AllTimeMax);
    }

    [Fact]
    public void PreTenantStoreFile_Reloads_UnderTheLocalTenant()
    {
        // A version-1 file (the pre-tenant single-object shape) written by an older build migrates its numbers
        // into the Local tenant, so a self-host upgrade keeps its all-time peak rather than quarantining it.
        var legacy = """
        {
          "LiveAllTimeMax": 42,
          "LiveAllTimeMaxAtUtc": "2026-07-11T20:00:00Z",
          "WorkingAllTimeMax": 9,
          "WorkingAllTimeMaxAtUtc": "2026-07-11T20:00:00Z",
          "Hours": { "2026-07-11T20": { "MaxLive": 42, "MaxWorking": 9, "DistinctSessions": 42, "DistinctMachines": 3, "DistinctRepos": 5 } },
          "CurrentHourKey": "2026-07-11T20",
          "CurrentSessions": [],
          "CurrentMachines": [],
          "CurrentRepos": []
        }
        """;
        File.WriteAllText(_path, legacy);

        var s = new GatewaySessionConcurrencyStats(_path);
        var snap = s.Snapshot(T0.AddMinutes(1)); // default tenant == Local
        Assert.Equal(42, snap.Live.AllTimeMax);
        Assert.Equal(9, snap.Working.AllTimeMax);
        Assert.Equal(42, Assert.Single(snap.Hourly).Sessions);
    }
}
