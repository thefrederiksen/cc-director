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
    /// status. "Unsupported" and "the transcript has not appeared yet" are different facts and the voice
    /// service now treats them differently - one is terminal, the other is retried - so collapsing them
    /// would trade one lie for another.
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

    /// <summary>
    /// THE OTHER HALF (found in review): a RESOLVED path is not a proven read either.
    ///
    /// Copilot and OpenCode resolve to a GLOBAL SQLite store, which exists or does not exist for reasons
    /// that have nothing to do with this session - and both readers answer an empty history both for a store
    /// with no repository match AND for a database error they caught. Stamping "ok" on that recreates the
    /// exact false success this whole change removes, so an empty conversation gets its own status.
    ///
    /// Gemini is the case asserted here: it goes down the same branch with a deliberately null transcript
    /// path (it reads the terminal buffer), and an embedded test session's buffer is empty - so it produces
    /// a resolved-but-empty read deterministically, on any machine.
    ///
    /// COPILOT AND OPENCODE ARE DELIBERATELY NOT ASSERTED HERE, and the reason is worth writing down: their
    /// readers open the DEVELOPER'S OWN store at CopilotHistoryReader.DefaultDatabasePath and match by
    /// repository path, so what this test would assert depends on what happens to be in that database on the
    /// machine running it. A first version of this test did include them and failed on exactly that - the
    /// local Copilot store answered a non-empty history for a temporary directory. A test whose verdict
    /// moves with the developer's machine is not evidence, so the store-backed agents are covered by the
    /// status-handling tests in WingmanVoiceServiceTests (which drive "empty_history" directly) rather than
    /// by reading a real database here.
    /// </summary>
    [Theory]
    [InlineData(Core.Agents.AgentKind.Gemini)]
    public void Turns_ResolvedSourceWithNoConversation_ReportsEmptyHistory_NotOk(Core.Agents.AgentKind agent)
    {
        var (sm, session) = NewSession();
        try
        {
            session.AgentKind = agent;

            var resp = Read(sm, session);

            Assert.NotEqual("ok", resp.Status);                    // the false success is gone...
            Assert.Contains(resp.Status, new[] { "empty_history", "no_transcript" });
            Assert.Empty(resp.Widgets);
        }
        finally { sm.Dispose(); }
    }
}
