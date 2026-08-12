using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2561: the <c>turns</c> verb must not report an UNRESOLVED TRANSCRIPT as a successful read of an
/// empty conversation.
///
/// The non-Claude-Code branch used to stamp <c>Status = "ok"</c> unconditionally, but
/// <c>SessionHistoryReader.ReadAll</c> answers <c>ConversationHistory.Empty</c> whenever
/// <c>ResolveTranscriptPath</c> returns null - which is exactly what the per-agent locators
/// (<c>PiSessionLocator</c>, <c>CodexRolloutLocator</c>, <c>GrokSessionLocator</c>) do before an agent has
/// written its first transcript. So a Pi / Codex / Grok session whose transcript had not been located
/// returned "ok" with no widgets, and the ONE field that could have told the caller otherwise agreed.
///
/// The cost was voice narration going permanently silent: it read this verb, saw no text widget, and
/// recorded the non-failure "nothing to narrate", which is never retried and raises nothing anywhere. A Pi
/// session observed on 12 August sat silent for 48 minutes while the roster showed it "Preparing voice".
/// </summary>
[Collection("DirectorRoot")]
public sealed class TurnsVerbUnresolvedTranscriptTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        return (sm, session);
    }

    private static TurnsResponse Read(SessionManager sm, Session session)
    {
        var command = new DirectorCommand { CommandId = "t1", Verb = "turns", SessionId = session.Id.ToString() };
        var result = SessionReadExecutor.Turns(sm, command);
        Assert.True(result.Ok);   // a failed READ still rides a successful command result - that is the trap
        return JsonSerializer.Deserialize<TurnsResponse>(result.BodyJson!, Json)!;
    }

    /// <summary>
    /// A supported non-Claude agent whose transcript has not been located yet. The session is embedded on a
    /// throwaway repo path with a fresh id, so no locator can resolve a transcript for it - the exact state a
    /// freshly-spawned Pi session is in before it writes its first turn.
    /// </summary>
    [Theory]
    [InlineData(Core.Agents.AgentKind.Pi)]
    [InlineData(Core.Agents.AgentKind.Codex)]
    [InlineData(Core.Agents.AgentKind.Grok)]
    public void Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(Core.Agents.AgentKind agent)
    {
        var (sm, session) = NewSession();
        try
        {
            session.AgentKind = agent;

            var resp = Read(sm, session);

            Assert.Equal("no_transcript", resp.Status);
            Assert.False(string.IsNullOrWhiteSpace(resp.Error));   // and it says WHY, so a caller can log it
            Assert.Empty(resp.Widgets);
        }
        finally { sm.Dispose(); }
    }

    /// <summary>
    /// The negative control: an agent that exposes no conversation history at all keeps its own distinct
    /// status. "Unsupported" and "the transcript has not appeared yet" are different facts and a caller may
    /// reasonably treat them differently - collapsing them would trade one lie for another.
    /// </summary>
    [Fact]
    public void Turns_AgentWithNoHistoryProvider_StillReportsUnsupported()
    {
        var (sm, session) = NewSession();
        try
        {
            session.AgentKind = Core.Agents.AgentKind.Cursor;

            var resp = Read(sm, session);

            Assert.Equal("unsupported", resp.Status);
        }
        finally { sm.Dispose(); }
    }
}
