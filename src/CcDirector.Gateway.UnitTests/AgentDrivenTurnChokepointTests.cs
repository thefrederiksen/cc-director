using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1636: that the Session choke point actually EMITS the agent-driven tally the Gateway aggregates.
///
/// The aggregator tests set InputStatsDto.AgentDrivenTurns by hand, which proves the fold but proves
/// nothing about whether anything ever produces that value - a live consumer reading a field no producer
/// assigns is the most common defect in this codebase, and it always looks like green tests. These drive
/// the real Session.SendTextAsync with a real SendSource and read the real snapshot.
/// </summary>
public sealed class AgentDrivenTurnChokepointTests
{
    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        return (sm, session);
    }

    // One agent prompting another is a real turn and must be counted - on its own lane.
    [Fact]
    public async Task AgentSend_IsCountedAsAnAgentDrivenTurn()
    {
        var (sm, session) = NewSession();
        try
        {
            const string text = "a manager prompting its worker";
            await session.SendTextAsync(text, SendSource.Agent);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(1, snap.AgentDrivenTurns);
            Assert.Equal(text.Length, snap.AgentDrivenCharacters);
            // ...and NEVER into the human buckets: that is the whole point of the separate lane.
            Assert.Empty(snap.Buckets);
        }
        finally { sm.Dispose(); }
    }

    // Text the product authored itself carries nobody's decision. It is not a turn for anyone.
    [Fact]
    public async Task FrameworkSend_IsNotCountedAtAll()
    {
        var (sm, session) = NewSession();
        try
        {
            await session.SendTextAsync("handover context the product wrote", SendSource.Framework);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(0, snap.AgentDrivenTurns);
            Assert.Equal(0, snap.AgentDrivenCharacters);
            Assert.Empty(snap.Buckets);
        }
        finally { sm.Dispose(); }
    }

    // A human turn stays a human turn: the agent lane must not swallow it.
    [Fact]
    public async Task HumanSend_IsCountedAsAHumanTurn_NotAnAgentOne()
    {
        var (sm, session) = NewSession();
        try
        {
            await session.SendTextAsync("what I said", SendSource.UserInput, InputOrigin.DesktopVoice);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(0, snap.AgentDrivenTurns);
            var bucket = Assert.Single(snap.Buckets);
            Assert.Equal("voice", bucket.Modality);
            Assert.Equal("desktop", bucket.Surface);
            Assert.Equal(1, bucket.Turns);
        }
        finally { sm.Dispose(); }
    }

    // A session driven only by other agents has no buckets, so IsEmpty must not hide it from the wire -
    // those are exactly the sessions the agent-to-agent tally is about.
    [Fact]
    public async Task AgentOnlySession_IsNotReportedAsEmpty()
    {
        var (sm, session) = NewSession();
        try
        {
            Assert.True(session.InputStats.IsEmpty);
            await session.SendTextAsync("worker, do the thing", SendSource.Agent);
            Assert.False(session.InputStats.IsEmpty);
        }
        finally { sm.Dispose(); }
    }

    // A fleet message to a session on ANOTHER Director arrives as an ordinary prompt over the tunnel, with
    // no Surface and the source defaulting to UserInput - indistinguishable from a human prompt. Without
    // the DTO marker the SAME message counts as agent-driven or not depending only on whether the two
    // sessions happened to share a Director, which is not a property of the work at all.
    [Fact]
    public async Task RelayedFleetPrompt_MarkedAgentDriven_IsCountedAsAnAgentTurn()
    {
        var (sm, session) = NewSession();
        try
        {
            var request = new Gateway.Contracts.PromptRequest
            {
                Text = "a manager on another machine, prompting its worker",
                AppendEnter = true,
                AgentDriven = true, // what the Gateway's fleet-message relay stamps on an agent-to-agent prompt
            };

            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, request);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(1, snap.AgentDrivenTurns);
            Assert.Empty(snap.Buckets); // never a human turn
        }
        finally { sm.Dispose(); }
    }

    // An agent-driven prompt must not be counted as human even if a Surface rides along with it: the
    // marker decides, not the surface.
    [Fact]
    public async Task RelayedFleetPrompt_WithASurface_IsStillNotAHumanTurn()
    {
        var (sm, session) = NewSession();
        try
        {
            var request = new Gateway.Contracts.PromptRequest
            {
                Text = "still an agent",
                AppendEnter = true,
                AgentDriven = true,
                Surface = "phone",
            };

            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, request);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(1, snap.AgentDrivenTurns);
            Assert.Empty(snap.Buckets);
        }
        finally { sm.Dispose(); }
    }

    // The control: an ordinary human prompt through the same entry point is still a human turn.
    [Fact]
    public async Task Prompt_NotMarkedAgentDriven_IsStillAHumanTurn()
    {
        var (sm, session) = NewSession();
        try
        {
            var request = new Gateway.Contracts.PromptRequest
            {
                Text = "me, typing on my phone",
                AppendEnter = true,
                Surface = "phone",
            };

            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, request);

            var snap = session.InputStats.Snapshot();
            Assert.Equal(0, snap.AgentDrivenTurns);
            var bucket = Assert.Single(snap.Buckets);
            Assert.Equal("typed", bucket.Modality);
            Assert.Equal("phone", bucket.Surface);
        }
        finally { sm.Dispose(); }
    }

    // The tally survives a Director restart restore, or every restart would silently reset the fleet's
    // driving to zero while the human's numbers carried on.
    [Fact]
    public async Task AgentDrivenTally_SurvivesASeedRoundTrip()
    {
        var (sm, session) = NewSession();
        try
        {
            await session.SendTextAsync("one", SendSource.Agent);
            await session.SendTextAsync("two", SendSource.Agent);
            var saved = session.InputStats.Snapshot();

            var (sm2, restored) = NewSession();
            try
            {
                restored.InputStats.Seed(saved);
                var snap = restored.InputStats.Snapshot();
                Assert.Equal(2, snap.AgentDrivenTurns);
                Assert.Equal(6, snap.AgentDrivenCharacters);
            }
            finally { sm2.Dispose(); }
        }
        finally { sm.Dispose(); }
    }

    // ---------- SOMETHING HAS TO SET THE MARKER (ruling R12, "Clean up Your Throttle", 2026-09-05) ----------
    //
    // Every test above proves the Director does the right thing WHEN TOLD. Nothing proved anybody ever told
    // it, and nobody did: `AgentDriven` had no producer anywhere in the product, so every fleet message
    // reached the Director as an ordinary UserInput with no origin. It was then left out of the person's
    // voice-versus-typed figures only because no surface resolved for it - the right answer by the wrong
    // road - and left out of the agent-driven lane, which exists to count exactly these, altogether. Over
    // the owner's week of 2026-W35 that was 292 of 296 fleet messages, missing from both numbers.
    //
    // This file's own opening paragraph names that failure - "a live consumer reading a field no producer
    // assigns is the most common defect in this codebase, and it always looks like green tests" - and then
    // the suite went on to demonstrate it. So the producer is pinned here, beside the consumer, in source.
    //
    // WHY A SOURCE TEST. Both producers are inside route lambdas in GatewayEndpoints.cs, which only comes
    // alive with the whole host booted; that harness is the parked suite. What has to be guarded is not the
    // wire behaviour - the tests above already cover it - but that the two fleet delivery paths keep
    // DECIDING. Counting the construction sites is what makes a third one fail loudly instead of quietly
    // defaulting to "a person typed this".

    [Fact]
    public void EveryPromptTheGatewayBuildsForTheFleet_SaysWhetherAnAgentDroveIt()
    {
        var endpoints = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Api", "GatewayEndpoints.cs"));

        // Two, and only two, places in the Gateway build a prompt to deliver: the one-to-one fleet message
        // and the fanout that serves a broadcast. A third would be a new way for a turn to reach a session,
        // and it must answer this question rather than inherit an answer.
        var sites = System.Text.RegularExpressions.Regex.Matches(endpoints, @"new PromptRequest");
        Assert.Equal(2, sites.Count);

        // The one-to-one message route: reached only with a session key, so the sender is an agent by
        // construction and the marker is unconditional.
        Assert.Contains("AppendEnter = true,\r\n                WaitForIdle = req.WaitForIdle,", endpoints);
        Assert.Contains("AgentDriven = true,", endpoints);

        // The fanout: a device key acts for the ACCOUNT - a person broadcasting from the desktop or the
        // phone - so this one turns on whether the caller authenticated AS a session. It must never read
        // req.FromSessionId, which the caller supplies and could use to decide whether its own turns count.
        Assert.Contains("AgentDriven = callingSession is not null,", endpoints);
        Assert.DoesNotContain("AgentDriven = req.FromSessionId", endpoints);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
