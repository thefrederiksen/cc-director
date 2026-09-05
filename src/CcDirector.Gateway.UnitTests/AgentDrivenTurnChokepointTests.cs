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

    // The source-reading test that used to sit here - counting "new PromptRequest" expressions in
    // GatewayEndpoints.cs and grepping for two assignments - was DELETED on the independent inspection of
    // phase two ("Clean up Your Throttle", 2026-09-05, finding I2-02): it was proof over the wrong surface.
    // A factory extraction would have failed it while a deserialization path accepting an untrusted
    // AgentDriven field stayed green, which is exactly the defect it did not catch. The producer is now
    // proven where it lives: CcDirector.Gateway.Tests PromptAttributionIsGatewayAuthoritativeTests posts
    // hostile bodies at the mapped route and reads what reaches the Director.
}
