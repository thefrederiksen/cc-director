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
        _path = Path.Combine(_dir, "gateway-stats.db");
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

    // A session driving a named agent CLI - the Agents breakdown ("how much Claude Code vs Codex").
    private static SessionDto SessionOnAgent(string id, string agent, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = Session(id, buckets);
        dto.Agent = agent;
        return dto;
    }

    private static AgentStatBucketDto? Agent(GatewayInputStatsAggregator agg, string agentName) =>
        agg.AgentTotals().FirstOrDefault(a => a.AgentName == agentName);


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

    [Fact]
    public void AgentTotals_AttributeTurnsToAgent_SplitByModality_AndRankByTurns()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        // The owner's question: how much Claude Code compared to Codex. Claude Code gets more, so it ranks first.
        agg.Observe(SessionOnAgent("s1", "ClaudeCode", ("voice", "phone", 6, 600)));
        agg.Observe(SessionOnAgent("s2", "ClaudeCode", ("typed", "desktop", 2, 40)));
        agg.Observe(SessionOnAgent("s3", "Codex", ("typed", "cockpit", 3, 90)));

        var ranked = agg.AgentTotals();
        Assert.Equal(2, ranked.Count);
        Assert.Equal("Claude Code", ranked[0].AgentName);   // 8 turns - ranked first
        Assert.Equal("Codex", ranked[1].AgentName);         // 3 turns

        var cc = ranked[0];
        Assert.Equal("ClaudeCode", cc.Agent);               // the raw token stays the grouping key
        Assert.Equal(8, cc.Turns);
        Assert.Equal(6, cc.VoiceTurns);
        Assert.Equal(2, cc.TypedTurns);
        Assert.Equal(640, cc.Characters);
        Assert.Equal(2, cc.Sessions);                       // two distinct sessions drove it
    }

    [Fact]
    public void AgentTotals_DistinctSessions_NoDoubleCountAcrossRepush_AndSurviveRestart()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(SessionOnAgent("s1", "Codex", ("voice", "phone", 3, 120)));
        agg.Observe(SessionOnAgent("s1", "Codex", ("voice", "phone", 3, 120))); // re-push, no change
        agg.Observe(SessionOnAgent("s1", "Codex", ("voice", "phone", 5, 200))); // grew by 2

        var codex = Agent(agg, "Codex");
        Assert.NotNull(codex);
        Assert.Equal(5, codex!.Turns);
        Assert.Equal(1, codex.Sessions); // the same session id must count once, not three times

        // Gateway restart: the per-agent tally (and its distinct-session set) reload from disk.
        var reloaded = new GatewayInputStatsAggregator(_path);
        var codex2 = Agent(reloaded, "Codex");
        Assert.NotNull(codex2);
        Assert.Equal(5, codex2!.Turns);
        Assert.Equal(200, codex2.Characters);
        Assert.Equal(1, codex2.Sessions);
    }

    [Fact]
    public void AgentTotals_SessionWithNoAgent_IsCountedAsUnknown_NeverDropped()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        // A Director that reported no agent must not silently vanish from the breakdown - the turns are real.
        agg.Observe(Session("s1", ("typed", "desktop", 4, 80)));

        var unknown = Agent(agg, "(unknown)");
        Assert.NotNull(unknown);
        Assert.Equal("", unknown!.Agent);
        Assert.Equal(4, unknown.Turns);
    }

    [Fact]
    public void AgentsSince_IsStampedOnFirstObservation_AndNeverMovesAfterwards()
    {
        var first = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc);
        var agg = new GatewayInputStatsAggregator(_path);
        Assert.Equal("", agg.AgentsSinceUtc); // nothing observed yet - no claim about a window

        agg.Observe(SessionOnAgent("s1", "ClaudeCode", ("typed", "desktop", 1, 10)), first);
        var stamped = agg.AgentsSinceUtc;
        Assert.NotEqual("", stamped);
        Assert.StartsWith("2026-07-15T09:00:00", stamped);

        // A later observation must NOT move the since-date, or the page would understate its own window.
        agg.Observe(SessionOnAgent("s1", "ClaudeCode", ("typed", "desktop", 9, 90)), first.AddHours(5));
        Assert.Equal(stamped, agg.AgentsSinceUtc);

        // It survives a Gateway restart, so the window does not reset every time the Gateway starts.
        var reloaded = new GatewayInputStatsAggregator(_path);
        Assert.Equal(stamped, reloaded.AgentsSinceUtc);
    }

    [Fact]
    public void AgentTotals_TrackTheSameTurnsAsTheTotals_FromTheSameDeltas()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        // The agent tally rides the SAME high-water deltas as the totals, so for turns folded while it was
        // live, the two must agree - the breakdown is a split of the totals, not a second count of them.
        agg.Observe(SessionOnAgent("s1", "ClaudeCode", ("voice", "phone", 6, 600)));
        agg.Observe(SessionOnAgent("s2", "Codex", ("typed", "desktop", 4, 40)));

        var totalTurns = agg.CurrentTotals().Buckets.Sum(b => b.Turns);
        var agentTurns = agg.AgentTotals().Sum(a => a.Turns);
        Assert.Equal(totalTurns, agentTurns);
        Assert.Equal(10, agentTurns);
    }

    // ==================== Issue #1636: the agent-to-agent lane ====================

    // A session being driven by other agents: the fleet prompting itself.
    private static SessionDto AgentDrivenSession(string id, string agent, long turns, long chars,
        params (string modality, string surface, long turns, long chars)[] humanBuckets)
    {
        var dto = SessionOnAgent(id, agent, humanBuckets);
        dto.InputStats!.AgentDrivenTurns = turns;
        dto.InputStats.AgentDrivenCharacters = chars;
        return dto;
    }

    // THE TRAP this whole design exists to avoid. Agent turns are typed by definition, so folding them in
    // with the human's would drop the voice share overnight and break every comparison with history - a
    // number that moved because the definition moved, not because anything about the work did.
    [Fact]
    public void AgentDrivenTurns_NeverEnterTheHumanTotals()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        // 2 human voice turns, and 100 turns the fleet drove into the same session.
        agg.Observe(AgentDrivenSession("s1", "Codex", 100, 10_000, ("voice", "desktop", 2, 200)));

        // The human totals are untouched by the fleet's 100.
        Assert.Equal(2, agg.CurrentTotals().Buckets.Sum(b => b.Turns));
        Assert.Equal(2, Turns(agg.CurrentTotals(), "voice", "desktop"));
        Assert.Equal(200, agg.CurrentTotals().Buckets.Sum(b => b.Characters));

        // The voice share stays 100% of the human's driving, not 2%.
        Assert.Equal(2, Agent(agg, "Codex")!.VoiceTurns);
        Assert.Equal(2, Agent(agg, "Codex")!.Turns);

        // And the fleet's driving is reported on its own lane.
        Assert.Equal(100, Agent(agg, "Codex")!.AgentDrivenTurns);
        Assert.Equal((100, 10_000), agg.AgentDrivenUsage());
    }

    // The hourly "working day" series is about when the HUMAN worked. A fleet that ran all night must not
    // appear in it as the owner's hours.
    [Fact]
    public void AgentDrivenTurns_NeverEnterTheHourlySeries()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(AgentDrivenSession("s1", "Codex", 50, 5_000));

        Assert.DoesNotContain(agg.HourlyTurns(), h => h.Turns > 0);
        Assert.Equal(50, agg.AgentDrivenUsage().Turns);
    }

    // A session driven ONLY by other agents has no human buckets at all - and those are exactly the
    // sessions this tally is about, so it must not be skipped by the buckets guard.
    [Fact]
    public void AgentDrivenSession_WithNoHumanTurns_IsStillCounted()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(AgentDrivenSession("worker-1", "Codex", 12, 1_200));

        Assert.Equal(12, agg.AgentDrivenUsage().Turns);
        Assert.Equal(12, Agent(agg, "Codex")!.AgentDrivenTurns);
        Assert.Equal(0, Agent(agg, "Codex")!.Turns);
    }

    [Fact]
    public void AgentDrivenTurns_RepeatedSnapshot_DoesNotDoubleCount()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(AgentDrivenSession("s1", "Codex", 10, 1_000));
        agg.Observe(AgentDrivenSession("s1", "Codex", 10, 1_000));
        agg.Observe(AgentDrivenSession("s1", "Codex", 14, 1_400));

        Assert.Equal(14, agg.AgentDrivenUsage().Turns);
        Assert.Equal(1_400, agg.AgentDrivenUsage().Characters);
    }

    // A Director restarted this session id with a fresh tally, so the reported count DROPPED. The whole
    // current count is new activity from zero - the same rule the human buckets follow.
    [Fact]
    public void AgentDrivenTurns_DroppedCount_IsTreatedAsFreshActivity()
    {
        var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(AgentDrivenSession("s1", "Codex", 10, 1_000));
        agg.Observe(AgentDrivenSession("s1", "Codex", 3, 300));

        Assert.Equal(13, agg.AgentDrivenUsage().Turns);
    }

    [Fact]
    public void AgentDrivenTurns_SurviveAGatewayRestart()
    {
        var first = new GatewayInputStatsAggregator(_path);
        first.Observe(AgentDrivenSession("s1", "Codex", 10, 1_000));

        var restarted = new GatewayInputStatsAggregator(_path);
        Assert.Equal(10, restarted.AgentDrivenUsage().Turns);
        Assert.Equal(10, Agent(restarted, "Codex")!.AgentDrivenTurns);

        // The high-water survived too, so re-observing the same counts adds nothing.
        restarted.Observe(AgentDrivenSession("s1", "Codex", 10, 1_000));
        Assert.Equal(10, restarted.AgentDrivenUsage().Turns);
    }

    // ==================== Issue #1633: the high-water lockout ====================
    //
    // These tests used to seed a legacy gateway-input-stats.json and drive the JSON Load path. That path is
    // GONE: the owner ruled the old numbers are not carried across, so the document is renamed aside UNREAD
    // and this store starts empty. Tests for a load that no longer happens would be testing nothing.
    //
    // The BEHAVIOUR they guarded is still live and is still tested below, re-expressed against SQLite. What
    // is deliberately no longer covered is the legacy-document shapes - a pre-tally store, and the hybrid
    // partially-attributed store - because neither state is reachable any more: high-water and agents_seeded
    // are now written in the same transaction, so a session can never have counted turns without having been
    // seeded. That is not a revert of #1647; #1647's fold, back-fill and guard are all ported and exercised.

    // The hazard the persisted seed-set exists to prevent. session_highwater survives a Gateway restart, so
    // without agents_seeded the first fold after one would back-fill every live session's high-water into its
    // agent a SECOND time and inflate the numbers a little more on every restart.
    //
    // This is the test that justifies the agents_seeded table existing at all: on a fresh database the
    // back-fill contributes nothing, so the table looks like dead weight until you restart.
    [Fact]
    public void BackFill_DoesNotDoubleCount_AcrossAGatewayRestart()
    {
        using (var first = new GatewayInputStatsAggregator(_path))
        {
            first.Observe(SessionOnAgent("codex-1", "Codex", ("typed", "unknown", 4, 400)));
            Assert.Equal(4, Agent(first, "Codex")!.Turns);
        }

        using var restarted = new GatewayInputStatsAggregator(_path);
        Assert.Equal(4, Agent(restarted, "Codex")!.Turns);

        // The same session, same counts, after a restart: its high-water is loaded and non-empty, which is
        // exactly the state an unguarded back-fill would fire against.
        restarted.Observe(SessionOnAgent("codex-1", "Codex", ("typed", "unknown", 4, 400)));
        Assert.Equal(4, Agent(restarted, "Codex")!.Turns);
    }

    // The back-fill is a one-shot within a run too.
    [Fact]
    public void BackFill_DoesNotDoubleCount_OnRepeatedFold()
    {
        using var agg = new GatewayInputStatsAggregator(_path);
        agg.Observe(SessionOnAgent("codex-1", "Codex", ("typed", "unknown", 4, 400)));
        agg.Observe(SessionOnAgent("codex-1", "Codex", ("typed", "unknown", 4, 400)));
        Assert.Equal(4, Agent(agg, "Codex")!.Turns);
    }


    // The back-fill must not invent turns for a session that never had any. A fresh session with an empty
    // high-water back-fills nothing, and the ordinary delta path does all the work - the control that says
    // the back-fill only ever recovers history that genuinely happened.
    [Fact]
    public void FreshSession_BackFillsNothing_AndStillCountsNormally()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnAgent("new-1", "Codex", ("typed", "desktop", 2, 20)));
        Assert.Equal(2, Agent(agg, "Codex")!.Turns);

        agg.Observe(SessionOnAgent("new-1", "Codex", ("typed", "desktop", 2, 20)));
        Assert.Equal(2, Agent(agg, "Codex")!.Turns);
    }

    // ---- The model dimension (issue #1637): which model actually did the work. ----

    private static SessionDto SessionOnModel(string id, string? model, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = Session(id, buckets);
        dto.CurrentModel = model;
        return dto;
    }

    private static ModelStatBucketDto? Model(GatewayInputStatsAggregator agg, string? model) =>
        agg.ModelTotals().FirstOrDefault(m => m.Model == model);

    [Fact]
    public void Model_ReportedByTheDirector_IsAttributedToThatModel()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnModel("s-1", "claude-opus-4-8", ("typed", "desktop", 3, 30)));
        agg.Observe(SessionOnModel("s-2", "gpt-5.5", ("voice", "phone", 2, 20)));

        Assert.Equal(3, Model(agg, "claude-opus-4-8")!.Turns);
        Assert.Equal(3, Model(agg, "claude-opus-4-8")!.TypedTurns);
        Assert.Equal(30, Model(agg, "claude-opus-4-8")!.Characters);
        Assert.Equal(2, Model(agg, "gpt-5.5")!.VoiceTurns);
    }

    [Fact]
    public void Model_NotYetRecordedByTheAgent_FoldsAsNullAndIsNotAModelNamedNothing()
    {
        // The permanent state, not a corner case: CurrentModel is records-only, so EVERY session's first
        // turn folds before its agent has recorded a model, and it stays that way - this store records
        // forward and never revisits a written row. The null bucket is therefore a real, honest answer.
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnModel("s-1", null, ("typed", "desktop", 4, 40)));

        Assert.Equal(4, Model(agg, null)!.Turns);
        // It must NOT become an identity spelled "" - that would rank among the models and render as a
        // model named nothing, which is the empty-string trap repo_id lives with and this column avoids.
        Assert.DoesNotContain(agg.ModelTotals(), m => m.Model == "");
    }

    [Fact]
    public void Model_SwitchedMidSession_AttributesEachTurnToTheModelThatProducedIt()
    {
        // The reason the producer re-reads the model at every turn-end rather than trusting the launch
        // flag: the owner switches model mid-session (Claude's /model). The turns before the switch belong
        // to the old model and the turns after belong to the new one, and neither may be restated.
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnModel("s-1", "claude-opus-4-8", ("typed", "desktop", 5, 50)));
        agg.Observe(SessionOnModel("s-1", "claude-sonnet-5", ("typed", "desktop", 8, 80)));

        // Only the INCREASE is folded, so the switch moves 3 turns to the new model and leaves the first 5
        // where they were earned. A design that stamped the session's current model over its history would
        // report 8 and 0 here.
        Assert.Equal(5, Model(agg, "claude-opus-4-8")!.Turns);
        Assert.Equal(3, Model(agg, "claude-sonnet-5")!.Turns);
    }

    [Fact]
    public void Model_DifferingOnlyByCase_IsOneModel_DecidedInCSharpAndNeverBySqliteCollation()
    {
        // The same rule as the repository dimension, and the reason the column is a surrogate id: SQLite's
        // default BINARY collation would answer case-SENSITIVELY and split one model into two. Identity is
        // decided by the in-memory OrdinalIgnoreCase map, so it cannot.
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnModel("s-1", "claude-opus-4-8", ("typed", "desktop", 2, 20)));
        agg.Observe(SessionOnModel("s-2", "Claude-Opus-4-8", ("typed", "desktop", 3, 30)));

        var models = agg.ModelTotals().Where(m => m.Model is not null).ToList();
        Assert.Single(models);
        Assert.Equal(5, models[0].Turns);
        // First-seen spelling wins, exactly as a Dictionary keeps the key it was first given.
        Assert.Equal("claude-opus-4-8", models[0].Model);
    }

    [Fact]
    public void Model_SurvivesAGatewayRestart_WithItsIdentityAndItsTurnsIntact()
    {
        using (var first = new GatewayInputStatsAggregator(_path))
            first.Observe(SessionOnModel("s-1", "claude-opus-4-8", ("typed", "desktop", 6, 60)));

        // A restart rebuilds the identity mirror FROM the database. If model_identity were not loaded, the
        // next fold would mint a SECOND id for the same model and split its turns in two.
        using var reopened = new GatewayInputStatsAggregator(_path);
        reopened.Observe(SessionOnModel("s-2", "claude-opus-4-8", ("typed", "desktop", 4, 40)));

        var models = reopened.ModelTotals().Where(m => m.Model is not null).ToList();
        Assert.Single(models);
        Assert.Equal(10, models[0].Turns);
    }

    [Fact]
    public void ModelTurns_SumToTheTotalTurns_IncludingTheNullBucket()
    {
        // The property that makes the page trustworthy: every counted turn is in exactly one model bucket.
        // If the null bucket were ever dropped or filtered, this would break - and a reader would see model
        // turns that quietly fail to add up to the totals.
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(SessionOnModel("s-1", "claude-opus-4-8", ("typed", "desktop", 3, 30)));
        agg.Observe(SessionOnModel("s-2", null, ("typed", "desktop", 4, 40)));
        agg.Observe(SessionOnModel("s-3", "gpt-5.5", ("voice", "phone", 5, 50)));

        var totals = agg.CurrentTotals();
        var modelTurns = agg.ModelTotals().Sum(m => m.Turns);

        Assert.Equal(totals.Buckets.Sum(b => b.Turns), modelTurns);
        Assert.Equal(12, modelTurns);
    }

    [Fact]
    public void Prune_ArchivingOldRows_KeepsEveryModelsTurnsWhereTheyWere()
    {
        // The ninety-day fold is where this dimension would die quietly. PruneLocked collapses detail rows
        // into one archive row per grouping key, so model_id must be in BOTH its select and its group by:
        // missing from the select, every archived turn silently becomes "model unknown"; missing from the
        // group by, different models collapse into one row under an arbitrary id. Either way the loss lands
        // ninety days after the change that caused it, against real data, with nothing to point at.
        //
        // The aggregator's own comment claims this works. This test is what makes the claim checkable.
        var agg = new GatewayInputStatsAggregator(_path);
        var old = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var now = old.AddDays(200); // Comfortably past the ninety-day retention window.

        agg.Observe(SessionOnModel("old-1", "claude-opus-4-8", ("typed", "desktop", 5, 50)), old);
        agg.Observe(SessionOnModel("old-2", "gpt-5.5", ("voice", "phone", 3, 30)), old);
        agg.Observe(SessionOnModel("old-3", null, ("typed", "desktop", 2, 20)), old);

        // Two rows for one model, so the archive fold has something to actually merge rather than just
        // copy - a per-model SUM that never adds two rows together would not notice a broken group by.
        agg.Observe(SessionOnModel("old-4", "claude-opus-4-8", ("typed", "desktop", 4, 40)), old);

        var beforePrune = agg.ModelTotals().ToDictionary(m => m.Model ?? "(null)", m => m.Turns);

        // This fold carries a row, which is what triggers the prune.
        agg.Observe(SessionOnModel("new-1", "claude-sonnet-5", ("typed", "desktop", 1, 10)), now);

        // FIRST: prove the prune actually fired. Without this the test could pass by doing nothing at all -
        // an assertion that cannot fail is not coverage. HourlyTurns excludes the archive marker, so the old
        // hour disappearing from it IS the archiving having happened.
        Assert.DoesNotContain(agg.HourlyTurns(), h => h.Hour == "2026-01-01T09");

        // THEN: the numbers are untouched. An all-time tally must not shrink or move between models because
        // detail was pruned - that is the entire point of archiving rather than deleting.
        var afterPrune = agg.ModelTotals().ToDictionary(m => m.Model ?? "(null)", m => m.Turns);

        Assert.Equal(9, beforePrune["claude-opus-4-8"]);
        Assert.Equal(9, afterPrune["claude-opus-4-8"]);
        Assert.Equal(3, afterPrune["gpt-5.5"]);
        Assert.Equal(2, afterPrune["(null)"]);
        Assert.Equal(1, afterPrune["claude-sonnet-5"]);

        // And nothing leaked between buckets: every pruned turn is still exactly where it was earned.
        // 9 opus + 3 gpt + 2 unknown = 14, the whole of what was folded before the cutoff.
        Assert.Equal(14, afterPrune.Where(kv => kv.Key != "claude-sonnet-5").Sum(kv => kv.Value));
    }

    [Fact]
    public void ModelsSinceUtc_IsStampedByTheMigration_SoANullModelCanBeRead()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        // A page cannot interpret a null model without this: it separates "folded before the dimension
        // existed" from "the agent had not recorded a model yet". Both store null.
        Assert.False(string.IsNullOrEmpty(agg.ModelsSinceUtc));
        Assert.True(DateTime.TryParse(agg.ModelsSinceUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));
    }
}
