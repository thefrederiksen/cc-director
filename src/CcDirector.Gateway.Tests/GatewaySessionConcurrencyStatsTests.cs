using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the DevThrottle Stats fleet-concurrency tracker: both series (live + working), the
/// all-time peak, the derived weekly max, restart durability, and hourly-history pruning.
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

    [Fact]
    public void Observe_TracksCurrentPeakAndHourly_ForBothSeries()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(liveCount: 10, workingCount: 3, T0);
        s.Observe(liveCount: 28, workingCount: 7, T0.AddMinutes(10));
        s.Observe(liveCount: 20, workingCount: 5, T0.AddMinutes(20)); // lower - the peak must stand

        var snap = s.Snapshot(T0.AddMinutes(21));
        Assert.Equal(20, snap.Live.Current);
        Assert.Equal(28, snap.Live.AllTimeMax);
        Assert.Equal(5, snap.Working.Current);
        Assert.Equal(7, snap.Working.AllTimeMax);
        // A single clock hour holds that hour's max for each series.
        Assert.Single(snap.Live.Hourly);
        Assert.Equal("2026-07-11T20", snap.Live.Hourly[0].Hour);
        Assert.Equal(28, snap.Live.Hourly[0].Max);
        Assert.Equal(7, snap.Working.Hourly[0].Max);
    }

    [Fact]
    public void Observe_CapsWorkingAtLive()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(liveCount: 5, workingCount: 9, T0); // working can never exceed live
        var snap = s.Snapshot(T0);
        Assert.Equal(5, snap.Working.Current);
        Assert.Equal(5, snap.Working.AllTimeMax);
    }

    [Fact]
    public void WeeklyMax_IsMaxOverLast7Days_WhileAllTimeKeepsTheOlderPeak()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(liveCount: 40, workingCount: 10, T0.AddDays(-10)); // older than a week
        s.Observe(liveCount: 25, workingCount: 6, T0.AddDays(-2));   // within the week
        s.Observe(liveCount: 22, workingCount: 5, T0);              // within the week

        var snap = s.Snapshot(T0);
        Assert.Equal(40, snap.Live.AllTimeMax); // all-time remembers the older peak
        Assert.Equal(25, snap.Live.WeeklyMax);  // weekly only sees the last 7 days
    }

    [Fact]
    public void PeaksAndHistory_SurviveRestart()
    {
        var a = new GatewaySessionConcurrencyStats(_path);
        a.Observe(liveCount: 30, workingCount: 8, T0);

        var b = new GatewaySessionConcurrencyStats(_path); // reload from disk
        var snap = b.Snapshot(T0.AddMinutes(1));
        Assert.Equal(30, snap.Live.AllTimeMax);
        Assert.Equal(8, snap.Working.AllTimeMax);
    }

    [Fact]
    public void OldHourlyBuckets_ArePruned_ButAllTimePeakRemains()
    {
        var s = new GatewaySessionConcurrencyStats(_path);
        s.Observe(liveCount: 12, workingCount: 3, T0.AddDays(-120)); // beyond the 90-day retention
        s.Observe(liveCount: 15, workingCount: 4, T0);              // triggers a prune at now = T0

        var snap = s.Snapshot(T0);
        Assert.Single(snap.Live.Hourly);                 // only the recent hour survives the history
        Assert.Equal("2026-07-11T20", snap.Live.Hourly[0].Hour);
        Assert.Equal(15, snap.Live.AllTimeMax);          // the peak is not history-derived, so it stands
    }
}
