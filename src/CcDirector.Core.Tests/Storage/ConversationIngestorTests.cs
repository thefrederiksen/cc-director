using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tests.Wingman; // BufferOnlyBackend (internal test stub)
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for <see cref="ConversationIngestor"/> (issue #1551): copying an agent's own transcript into
/// the durable <see cref="ConversationLog"/>, dropping tool calls, and joining each user message to
/// the origin event recorded at the Session choke point.
///
/// The session is pointed at a Claude-format transcript this test writes, via the same public pointer
/// the SessionStart hook uses - so the real SessionHistoryReader path is exercised, not a stub.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class ConversationIngestorTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public ConversationIngestorTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ingest-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Session NewSession()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        return manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());
    }

    /// <summary>Write a Claude-format transcript and point the session at it.</summary>
    private string WriteTranscript(Session session, params string[] jsonlLines)
    {
        var path = Path.Combine(_root, $"transcript-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, jsonlLines);
        session.UpdateClaudeSessionPointer("claude-session-id", path, "test");
        return path;
    }

    private static string TextLine(string type, string role, string text, DateTime ts) =>
        JsonSerializer.Serialize(new
        {
            type,
            timestamp = ts.ToString("O"),
            message = new { role, content = new[] { new { type = "text", text } } },
        });

    private static string UserLine(string text, DateTime ts) => TextLine("user", "user", text, ts);

    private static string AssistantLine(string text, DateTime ts) => TextLine("assistant", "assistant", text, ts);

    private static string AssistantToolLine(string toolName, DateTime ts) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = ts.ToString("O"),
            message = new
            {
                role = "assistant",
                content = new[] { new { type = "tool_use", id = "t1", name = toolName, input = new { a = 1 } } },
            },
        });

    private static IReadOnlyList<ConversationRecord> Recorded()
        => ConversationLog.Read(DateTime.UtcNow.Date.AddDays(-2), DateTime.UtcNow.Date.AddDays(1));

    [Fact]
    public void Ingest_copies_both_the_prompt_and_the_reply()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session, UserLine("fix the login bug", ts), AssistantLine("Fixed it.", ts.AddSeconds(5)));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        var records = Recorded();
        Assert.Equal(2, records.Count);
        Assert.Equal("user", records[0].Role);
        Assert.Equal("fix the login bug", records[0].Text);
        Assert.Equal("assistant", records[1].Role);
        Assert.Equal("Fixed it.", records[1].Text);
    }

    [Fact]
    public void Ingest_drops_tool_calls()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session,
            UserLine("read the file", ts),
            AssistantToolLine("Read", ts.AddSeconds(1)),
            AssistantLine("Here is what it says.", ts.AddSeconds(2)));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        var records = Recorded();
        // The tool_use message carries no Text part, so it is not recorded at all.
        Assert.Equal(2, records.Count);
        Assert.DoesNotContain(records, r => r.Text.Contains("tool_use"));
        Assert.Equal(new[] { "read the file", "Here is what it says." }, records.Select(r => r.Text));
    }

    [Fact]
    public void Ingest_is_idempotent_and_does_not_double_append()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session, UserLine("only once", ts));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);
        ingestor.Ingest(session);
        ingestor.Ingest(session);

        Assert.Single(Recorded());
    }

    [Fact]
    public void Ingest_picks_up_only_the_new_message_when_the_transcript_grows()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        var path = WriteTranscript(session, UserLine("first", ts));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        File.AppendAllLines(path, new[] { AssistantLine("reply", ts.AddSeconds(2)) });
        ingestor.Ingest(session);

        Assert.Equal(new[] { "first", "reply" }, Recorded().Select(r => r.Text));
    }

    // ===== the origin join =====

    [Fact]
    public void A_user_message_is_joined_to_the_origin_event_recorded_at_the_choke_point()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        // The submission crosses the choke point, writing an origin event...
        InputOriginLog.Write(new InputOriginRecord
        {
            TsUtc = ts,
            SessionId = session.Id.ToString(),
            Modality = "voice",
            Surface = "phone",
            CharCount = 9,
        });
        // ...and the agent records the same prompt in its transcript a moment later.
        WriteTranscript(session, UserLine("say hello", ts.AddSeconds(1)));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        var only = Assert.Single(Recorded());
        Assert.Equal("voice", only.Modality);
        Assert.Equal("phone", only.Surface);
    }

    [Fact]
    public void A_user_message_with_no_origin_event_is_unknown_not_guessed()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("typed straight into the terminal", DateTime.UtcNow));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        var only = Assert.Single(Recorded());
        Assert.Equal("unknown", only.Surface);
        Assert.Null(only.Modality);
    }

    [Fact]
    public void An_origin_event_far_away_in_time_is_not_matched()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        // An unrelated submission an hour earlier must not be claimed as this prompt's origin.
        InputOriginLog.Write(new InputOriginRecord
        {
            TsUtc = ts.AddHours(-1),
            SessionId = session.Id.ToString(),
            Modality = "voice",
            Surface = "phone",
            CharCount = 5,
        });
        WriteTranscript(session, UserLine("a different prompt", ts));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        Assert.Equal("unknown", Assert.Single(Recorded()).Surface);
    }

    [Fact]
    public void An_origin_event_from_another_session_is_never_matched()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        InputOriginLog.Write(new InputOriginRecord
        {
            TsUtc = ts,
            SessionId = Guid.NewGuid().ToString(), // a different session, same instant
            Modality = "voice",
            Surface = "phone",
            CharCount = 5,
        });
        WriteTranscript(session, UserLine("my own prompt", ts));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        Assert.Equal("unknown", Assert.Single(Recorded()).Surface);
    }

    [Fact]
    public void An_assistant_reply_carries_no_origin()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        InputOriginLog.Write(new InputOriginRecord
        {
            TsUtc = ts,
            SessionId = session.Id.ToString(),
            Modality = "typed",
            Surface = "desktop",
            CharCount = 5,
        });
        WriteTranscript(session, AssistantLine("I replied.", ts));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        var only = Assert.Single(Recorded());
        // The reply is near an origin event in time, but a reply has no origin - it must not borrow one.
        Assert.Null(only.Surface);
        Assert.Null(only.Modality);
    }

    /// <summary>
    /// Two Director sessions pointed at the SAME transcript must not each record it. Claude keeps a
    /// transcript per agent session so this is rare, but Copilot, OpenCode and Gemini resolve their
    /// history by repo out of one shared store, where two sessions on one repo read the identical
    /// conversation - so the dedupe is scoped to the source, not to the Director session.
    /// </summary>
    [Fact]
    public void Two_sessions_reading_the_same_transcript_record_it_once()
    {
        var ts = DateTime.UtcNow;
        var sessionA = NewSession();
        var path = WriteTranscript(sessionA, UserLine("shared conversation", ts));

        // A second Director session pointed at the very same transcript file.
        var sessionB = NewSession();
        sessionB.UpdateClaudeSessionPointer("claude-session-id", path, "test");

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(sessionA);
        ingestor.Ingest(sessionB);

        Assert.Single(Recorded());
    }

    /// <summary>
    /// Backfill for a live session is automatic and needs no separate pass: the first ingest reads the
    /// WHOLE transcript, so history written before the feature existed is copied on the next turn end.
    /// </summary>
    [Fact]
    public void First_ingest_backfills_the_whole_existing_conversation()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow.AddHours(-3);
        WriteTranscript(session,
            UserLine("an old prompt", ts),
            AssistantLine("an old reply", ts.AddSeconds(2)),
            UserLine("a later prompt", ts.AddMinutes(30)));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        Assert.Equal(
            new[] { "an old prompt", "an old reply", "a later prompt" },
            Recorded().Select(r => r.Text));
    }

    [Fact]
    public void A_real_agent_timestamp_is_marked_as_the_agents_own()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("stamped by claude", DateTime.UtcNow));

        using var ingestor = new ConversationIngestor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        ingestor.Ingest(session);

        Assert.True(Assert.Single(Recorded()).TimestampFromAgent);
    }
}
