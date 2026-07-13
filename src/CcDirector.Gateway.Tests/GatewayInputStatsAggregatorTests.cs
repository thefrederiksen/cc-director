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

    // A session with voice mode ON - the owner's definition of "using the wingman".
    private static SessionDto VoiceSession(string id, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = Session(id, buckets);
        dto.VoiceMode = true;
        return dto;
    }

    private static SessionDto SessionInRepo(string id, string repoPath, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = Session(id, buckets);
        dto.RepoPath = repoPath;
        return dto;
    }

    private static RepoStatBucketDto? Repo(GatewayInputStatsAggregator agg, string repoName) =>
        agg.RepoTotals().FirstOrDefault(r => r.RepoName == repoName);


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

    private static readonly DateTime H17 = new(2026, 7, 11, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime H18 = new(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HourlyTurns_LogsTurnDeltasByHourAndModality()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        // Hour 17: 3 voice/phone turns and 1 typed/desktop turn arrive.
        agg.Observe(Session("s1", ("voice", "phone", 3, 300)), H17);
        agg.Observe(Session("s2", ("typed", "desktop", 1, 50)), H17);
        // Hour 18: s1 grows by 2 more voice turns (5 total) - only the +2 delta lands in hour 18.
        agg.Observe(Session("s1", ("voice", "phone", 5, 500)), H18);

        var hours = agg.HourlyTurns();
        Assert.Equal(2, hours.Count);

        var h17 = hours.First(h => h.Hour == "2026-07-11T17");
        Assert.Equal(4, h17.Turns);       // 3 voice + 1 typed
        Assert.Equal(3, h17.VoiceTurns);
        Assert.Equal(1, h17.TypedTurns);
        Assert.Equal(350, h17.Characters);

        var h18 = hours.First(h => h.Hour == "2026-07-11T18");
        Assert.Equal(2, h18.Turns);       // only the +2 voice delta
        Assert.Equal(2, h18.VoiceTurns);
        Assert.Equal(0, h18.TypedTurns);
        Assert.Equal(200, h18.Characters);
    }

    [Fact]
    public void HourlyTurns_SurviveRestart()
    {
        var a = new GatewayInputStatsAggregator(_path);
        a.Observe(Session("s1", ("voice", "phone", 3, 300)), H17);

        var b = new GatewayInputStatsAggregator(_path); // reload from disk
        var hours = b.HourlyTurns();
        Assert.Single(hours);
        Assert.Equal("2026-07-11T17", hours[0].Hour);
        Assert.Equal(3, hours[0].VoiceTurns);
        Assert.Equal(300, hours[0].Characters);
    }

    [Fact]
    public void WingmanUsage_CountsVoiceModeSessionsAndTheirTurns()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(VoiceSession("s1", ("voice", "phone", 3, 120))); // voice mode on -> 3 wingman turns
        agg.Observe(Session("s2", ("typed", "desktop", 5, 100)));    // no voice mode -> not the wingman

        var w = agg.WingmanUsage();
        Assert.Equal(3, w.Turns);
        Assert.Equal(1, w.Sessions);
    }

    [Fact]
    public void WingmanUsage_CountsAVoiceModeSessionEvenWithNoInputYet()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        // Voice mode on, no turns typed yet - the session still counts as "using the wingman".
        agg.Observe(new SessionDto { SessionId = "s1", VoiceMode = true });

        var w = agg.WingmanUsage();
        Assert.Equal(0, w.Turns);
        Assert.Equal(1, w.Sessions);
    }

    [Fact]
    public void WingmanUsage_AddsOnlyTheTurnIncrease_AndDoesNotDoubleCountTheSession()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(VoiceSession("s1", ("voice", "phone", 2, 40)));
        agg.Observe(VoiceSession("s1", ("voice", "phone", 5, 100))); // grew by 3 while voice mode on

        var w = agg.WingmanUsage();
        Assert.Equal(5, w.Turns);
        Assert.Equal(1, w.Sessions); // still one distinct wingman session
    }

    [Fact]
    public void WingmanUsage_SurvivesRestart_AndDoesNotDoubleCountOnRepush()
    {
        var a = new GatewayInputStatsAggregator(_path);
        a.Observe(VoiceSession("s1", ("voice", "phone", 3, 120)));

        var b = new GatewayInputStatsAggregator(_path); // reload from disk
        Assert.Equal(3, b.WingmanUsage().Turns);
        Assert.Equal(1, b.WingmanUsage().Sessions);

        // The same live session re-pushes its current snapshot - must not re-add its turns.
        b.Observe(VoiceSession("s1", ("voice", "phone", 3, 120)));
        Assert.Equal(3, b.WingmanUsage().Turns);
        Assert.Equal(1, b.WingmanUsage().Sessions);
    }

    [Fact]
    public void RepoTotals_AttributeTurnsToRepo_SplitByModality_AndRankByTurns()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        // Two repos drive input; devthrottle gets more, so it must rank first.
        agg.Observe(SessionInRepo("s1", @"D:\ReposFred\devthrottle", ("voice", "phone", 6, 600)));
        agg.Observe(SessionInRepo("s2", @"D:\ReposFred\devthrottle", ("typed", "desktop", 2, 40)));
        agg.Observe(SessionInRepo("s3", @"C:\repos\mindzieWeb", ("typed", "cockpit", 3, 90)));

        var ranked = agg.RepoTotals();
        Assert.Equal(2, ranked.Count);
        Assert.Equal("devthrottle", ranked[0].RepoName);     // 8 turns - ranked first
        Assert.Equal("mindzieWeb", ranked[1].RepoName);      // 3 turns

        var dt = ranked[0];
        Assert.Equal(8, dt.Turns);
        Assert.Equal(6, dt.VoiceTurns);
        Assert.Equal(2, dt.TypedTurns);
        Assert.Equal(640, dt.Characters);
        Assert.Equal(2, dt.Sessions);                         // two distinct sessions drove it
        Assert.Equal(@"D:\ReposFred\devthrottle", dt.Repo);   // full path preserved
    }

    [Fact]
    public void RepoTotals_DistinctSessions_NoDoubleCountAcrossRepush_AndSurviveRestart()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(SessionInRepo("s1", @"D:\repos\app", ("voice", "phone", 3, 120)));
        agg.Observe(SessionInRepo("s1", @"D:\repos\app", ("voice", "phone", 3, 120))); // re-push, no change
        agg.Observe(SessionInRepo("s1", @"D:\repos\app", ("voice", "phone", 5, 200))); // grew by 2

        var app = Repo(agg, "app");
        Assert.NotNull(app);
        Assert.Equal(5, app!.Turns);
        Assert.Equal(1, app.Sessions); // the same session id must count once, not three times

        // Gateway restart: the per-repo tally (and its distinct-session set) reload from disk.
        var reloaded = new GatewayInputStatsAggregator(_path);
        var app2 = Repo(reloaded, "app");
        Assert.NotNull(app2);
        Assert.Equal(5, app2!.Turns);
        Assert.Equal(200, app2.Characters);
        Assert.Equal(1, app2.Sessions);
    }
}
