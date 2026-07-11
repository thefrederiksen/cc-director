using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway aggregation math (DevThrottle Stats): folding per-session tallies into all-time totals via
/// a high-water increment must never double-count a repeated snapshot, must add only the increase, must
/// keep a removed session's contribution, must treat a dropped count (a Director restart of the same
/// session id) as fresh activity, and must survive a Gateway restart with both the totals and the live
/// high-water intact.
/// </summary>
public sealed class GatewayInputStatsAggregatorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayInputStatsAggregatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-agg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "input-stats.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static SessionDto Session(string id, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = new SessionDto { SessionId = id, InputStats = new InputStatsDto() };
        foreach (var b in buckets)
            dto.InputStats!.Buckets.Add(new InputStatBucketDto { Modality = b.modality, Surface = b.surface, Turns = b.turns, Characters = b.chars });
        return dto;
    }

    private static long Turns(InputStatsDto dto, string modality, string surface) =>
        dto.Buckets.FirstOrDefault(b => b.Modality == modality && b.Surface == surface)?.Turns ?? 0;

    private static long Chars(InputStatsDto dto, string modality, string surface) =>
        dto.Buckets.FirstOrDefault(b => b.Modality == modality && b.Surface == surface)?.Characters ?? 0;

    [Fact]
    public void RepeatedSnapshot_DoesNotDoubleCount()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        var s = Session("s1", ("voice", "phone", 3, 120));

        agg.Observe(s);
        agg.Observe(s); // same counts again - a periodic re-push, must not double-count
        agg.Observe(s);

        var t = agg.CurrentTotals();
        Assert.Equal(3, Turns(t, "voice", "phone"));
        Assert.Equal(120, Chars(t, "voice", "phone"));
    }

    [Fact]
    public void GrowingSnapshot_AddsOnlyTheIncrease()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(Session("s1", ("typed", "desktop", 2, 40)));
        agg.Observe(Session("s1", ("typed", "desktop", 5, 100))); // grew by 3 turns / 60 chars

        var t = agg.CurrentTotals();
        Assert.Equal(5, Turns(t, "typed", "desktop"));
        Assert.Equal(100, Chars(t, "typed", "desktop"));
    }

    [Fact]
    public void TwoSessions_SumIntoTotals()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(Session("s1", ("voice", "phone", 4, 200)));
        agg.Observe(Session("s2", ("voice", "phone", 1, 50)));

        Assert.Equal(5, Turns(agg.CurrentTotals(), "voice", "phone"));
    }

    [Fact]
    public void Forget_KeepsContributionInTotals()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(Session("s1", ("voice", "phone", 4, 200)));

        agg.Forget("s1");

        Assert.Equal(4, Turns(agg.CurrentTotals(), "voice", "phone"));
        // A late duplicate push for the forgotten session starts a fresh high-water and would add again;
        // that is acceptable because RemoveSession is terminal - the session will not push after removal.
    }

    [Fact]
    public void DroppedCount_SameSessionId_CountsAsFreshActivity()
    {
        // A Director restarts and session s1 begins a NEW tally from zero (its reported count drops). The
        // new activity must be added, not ignored because it is below the old high-water.
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(Session("s1", ("typed", "cockpit", 10, 300)));
        agg.Observe(Session("s1", ("typed", "cockpit", 2, 40)));   // reset: 2 new turns of fresh activity

        Assert.Equal(12, Turns(agg.CurrentTotals(), "typed", "cockpit"));
        Assert.Equal(340, Chars(agg.CurrentTotals(), "typed", "cockpit"));
    }

    [Fact]
    public void Persistence_RestoresTotals_AndDoesNotDoubleCountLiveSession()
    {
        var agg1 = new GatewayInputStatsAggregator(_path);
        agg1.Observe(Session("s1", ("voice", "phone", 3, 120)));

        // Gateway restart: a new aggregator on the same path reloads totals AND the live high-water.
        var agg2 = new GatewayInputStatsAggregator(_path);
        Assert.Equal(3, Turns(agg2.CurrentTotals(), "voice", "phone"));

        // The same still-live session re-pushes its current snapshot after reconnect - must NOT be re-added.
        agg2.Observe(Session("s1", ("voice", "phone", 3, 120)));
        Assert.Equal(3, Turns(agg2.CurrentTotals(), "voice", "phone"));

        // ...and further growth adds only the increase.
        agg2.Observe(Session("s1", ("voice", "phone", 4, 160)));
        Assert.Equal(4, Turns(agg2.CurrentTotals(), "voice", "phone"));
        Assert.Equal(160, Chars(agg2.CurrentTotals(), "voice", "phone"));
    }
}
