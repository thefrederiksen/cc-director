using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tests.Wingman; // BufferOnlyBackend (internal test stub)
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for <see cref="ConversationIngestor"/> (issue #1551): capturing an agent's own transcript,
/// dropping tool calls, joining each user prompt to where it came from, and PUSHING the result to the
/// Gateway. The Director keeps no copy, so "recorded" here means "the Gateway accepted it".
///
/// The session is pointed at a Claude-format transcript this test writes, via the same public pointer
/// the SessionStart hook uses - so the real SessionHistoryReader path is exercised, not a stub.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class ConversationIngestorTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly FakeSink _sink = new();

    public ConversationIngestorTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ingest-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
        InputOriginBuffer.Clear();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        InputOriginBuffer.Clear();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Stands in for the Gateway: remembers what was pushed, and can refuse.</summary>
    private sealed class FakeSink : IPromptSink
    {
        public List<PromptRecord> Received { get; } = new();
        public int PushCalls { get; private set; }
        public bool Accept { get; set; } = true;

        public Task<bool> PushAsync(IReadOnlyList<PromptRecord> records)
        {
            PushCalls++;
            if (!Accept) return Task.FromResult(false);
            Received.AddRange(records);
            return Task.FromResult(true);
        }
    }

    private ConversationIngestor NewIngestor()
        => new(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }), _sink);

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

    private static string TextLine(string type, string role, string text, DateTime ts, string? contextId = null) =>
        JsonSerializer.Serialize(new
        {
            type,
            timestamp = ts.ToString("O"),
            sessionId = contextId,
            message = new { role, content = new[] { new { type = "text", text } } },
        });

    private static string UserLine(string text, DateTime ts, string? contextId = null)
        => TextLine("user", "user", text, ts, contextId);

    private static string AssistantLine(string text, DateTime ts, string? contextId = null)
        => TextLine("assistant", "assistant", text, ts, contextId);

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

    private IReadOnlyList<PromptRecord> Pushed => _sink.Received;

    // ===== capture =====

    [Fact]
    public async Task Both_the_prompt_and_the_reply_are_pushed_to_the_gateway()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session, UserLine("fix the login bug", ts), AssistantLine("Fixed it.", ts.AddSeconds(5)));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.Equal(2, Pushed.Count);
        Assert.Equal("user", Pushed[0].Role);
        Assert.Equal("fix the login bug", Pushed[0].Text);
        Assert.Equal("assistant", Pushed[1].Role);
        Assert.Equal("Fixed it.", Pushed[1].Text);
    }

    [Fact]
    public async Task Every_record_says_which_machine_it_came_from()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("hello", DateTime.UtcNow));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        // The Gateway holds the whole fleet - without this, two developers' records are the same.
        Assert.Equal(Environment.MachineName, Assert.Single(Pushed).Machine);
    }

    [Fact]
    public async Task Tool_calls_are_dropped()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session,
            UserLine("read the file", ts),
            AssistantToolLine("Read", ts.AddSeconds(1)),
            AssistantLine("Here is what it says.", ts.AddSeconds(2)));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        // The tool_use message carries no Text part, so it is never pushed.
        Assert.Equal(new[] { "read the file", "Here is what it says." }, Pushed.Select(r => r.Text));
    }

    [Fact]
    public async Task Re_ingesting_the_same_transcript_pushes_nothing_new()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("only once", DateTime.UtcNow));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);
        await ingestor.IngestAsync(session);
        await ingestor.IngestAsync(session);

        Assert.Single(Pushed);
    }

    [Fact]
    public async Task Only_the_new_message_is_pushed_when_the_transcript_grows()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        var path = WriteTranscript(session, UserLine("first", ts));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        File.AppendAllLines(path, new[] { AssistantLine("reply", ts.AddSeconds(2)) });
        await ingestor.IngestAsync(session);

        Assert.Equal(new[] { "first", "reply" }, Pushed.Select(r => r.Text));
    }

    [Fact]
    public async Task First_ingest_backfills_the_whole_existing_conversation()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow.AddHours(-3);
        WriteTranscript(session,
            UserLine("an old prompt", ts),
            AssistantLine("an old reply", ts.AddSeconds(2)),
            UserLine("a later prompt", ts.AddMinutes(30)));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.Equal(new[] { "an old prompt", "an old reply", "a later prompt" }, Pushed.Select(r => r.Text));
    }

    [Fact]
    public async Task Two_sessions_reading_the_same_transcript_push_it_once()
    {
        var ts = DateTime.UtcNow;
        var sessionA = NewSession();
        var path = WriteTranscript(sessionA, UserLine("shared conversation", ts));

        var sessionB = NewSession();
        sessionB.UpdateClaudeSessionPointer("claude-session-id", path, "test");

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(sessionA);
        await ingestor.IngestAsync(sessionB);

        Assert.Single(Pushed);
    }

    // ===== the Gateway is the only copy, so a refused push must not be forgotten =====

    [Fact]
    public async Task A_refused_push_is_retried_rather_than_lost()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("must not be lost", DateTime.UtcNow));

        using var ingestor = NewIngestor();

        // The Gateway is unreachable: nothing is stored, and the message must NOT be marked done -
        // the Director keeps no copy, so marking it would lose the prompt permanently.
        _sink.Accept = false;
        await ingestor.IngestAsync(session);
        Assert.Empty(Pushed);

        // The Gateway comes back and the prompt is still recorded on the next turn.
        _sink.Accept = true;
        await ingestor.IngestAsync(session);
        Assert.Equal("must not be lost", Assert.Single(Pushed).Text);
    }

    // ===== the origin join =====

    [Fact]
    public async Task A_prompt_is_joined_to_the_origin_noted_at_the_choke_point()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        // The submission crosses the choke point...
        InputOriginBuffer.Record(session.Id.ToString(), new InputOriginEvent(ts, "voice", "phone", 9));
        // ...and the agent records the same prompt in its transcript a moment later.
        WriteTranscript(session, UserLine("say hello", ts.AddSeconds(1)));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        var only = Assert.Single(Pushed);
        Assert.Equal("voice", only.Modality);
        Assert.Equal("phone", only.Surface);
    }

    [Fact]
    public async Task A_prompt_with_no_origin_is_unknown_not_guessed()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("typed straight into the terminal", DateTime.UtcNow));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        var only = Assert.Single(Pushed);
        Assert.Equal("unknown", only.Surface);
        Assert.Null(only.Modality);
    }

    [Fact]
    public async Task An_origin_far_away_in_time_is_not_matched()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        // An unrelated submission an hour earlier must not be claimed as this prompt's origin.
        InputOriginBuffer.Record(session.Id.ToString(), new InputOriginEvent(ts.AddHours(-1), "voice", "phone", 5));
        WriteTranscript(session, UserLine("a different prompt", ts));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.Equal("unknown", Assert.Single(Pushed).Surface);
    }

    [Fact]
    public async Task An_origin_from_another_session_is_never_matched()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        InputOriginBuffer.Record(Guid.NewGuid().ToString(), new InputOriginEvent(ts, "voice", "phone", 5));
        WriteTranscript(session, UserLine("my own prompt", ts));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.Equal("unknown", Assert.Single(Pushed).Surface);
    }

    [Fact]
    public async Task An_assistant_reply_carries_no_origin()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        InputOriginBuffer.Record(session.Id.ToString(), new InputOriginEvent(ts, "typed", "desktop", 5));
        WriteTranscript(session, AssistantLine("I replied.", ts));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        var only = Assert.Single(Pushed);
        // The reply is near an origin in time, but a reply has no origin - it must not borrow one.
        Assert.Null(only.Surface);
        Assert.Null(only.Modality);
    }

    // ===== the context id =====

    [Fact]
    public async Task Every_message_of_one_conversation_carries_the_same_context_id()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;
        WriteTranscript(session,
            UserLine("first", ts, "ctx-abc"),
            AssistantLine("reply", ts.AddSeconds(2), "ctx-abc"),
            UserLine("second", ts.AddSeconds(9), "ctx-abc"));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.Equal(3, Pushed.Count);
        Assert.All(Pushed, r => Assert.Equal("ctx-abc", r.ContextId));
    }

    [Fact]
    public async Task Clearing_the_context_starts_a_new_context_id_under_the_same_session()
    {
        var session = NewSession();
        var ts = DateTime.UtcNow;

        WriteTranscript(session, UserLine("before the clear", ts, "ctx-one"));
        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        // /clear: a new context id AND a new transcript file, repointed by the SessionStart hook.
        WriteTranscript(session, UserLine("after the clear", ts.AddMinutes(1), "ctx-two"));
        await ingestor.IngestAsync(session);

        Assert.Equal(2, Pushed.Count);
        Assert.Equal("ctx-one", Pushed[0].ContextId);
        Assert.Equal("ctx-two", Pushed[1].ContextId);
        // ...while the Director session id spans both, so the workstream stays joined up.
        Assert.All(Pushed, r => Assert.Equal(session.Id.ToString(), r.SessionId));
    }

    [Fact]
    public async Task A_context_id_is_recorded_as_absent_rather_than_invented()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("no context id on this line", DateTime.UtcNow));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        // Falls back to the transcript file's own name - which for a file-per-context agent IS the
        // context identity - rather than borrowing the Director session id and pretending.
        var only = Assert.Single(Pushed);
        Assert.NotNull(only.ContextId);
        Assert.NotEqual(session.Id.ToString(), only.ContextId);
    }

    [Fact]
    public async Task A_real_agent_timestamp_is_marked_as_the_agents_own()
    {
        var session = NewSession();
        WriteTranscript(session, UserLine("stamped by claude", DateTime.UtcNow));

        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        Assert.True(Assert.Single(Pushed).TimestampFromAgent);
    }

    // ===== the watermark must survive a RESTART - the one case it exists for =====
    //
    // Every dedupe test above runs in a single process, which is precisely where the watermark cannot
    // fail: .NET randomizes string.GetHashCode()'s seed per process, so a key built from it is stable
    // for exactly as long as the process lives - and the file outlives the process. The two tests below
    // are the only ones that cross that boundary, so they are the only ones that can catch it.
    //
    // The expected hashes here were computed OUTSIDE this codebase (python hashlib.sha256) so they
    // anchor to the standard algorithm rather than recording whatever our own implementation emits - a
    // golden that echoes the code it guards proves nothing.

    /// <summary>SHA-256 of "restart must not re-push this", first 32 hex characters.</summary>
    private const string HashOfRestartMessage = "835a674409124218ea244c65a37c8028";

    /// <summary>SHA-256 of "an old reply", first 32 hex characters.</summary>
    private const string HashOfOldReply = "69a9d10430492b3eba43929f72617200";

    [Fact]
    public async Task A_watermark_from_a_previous_process_still_dedupes_after_a_restart()
    {
        var session = NewSession();
        var ts = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        const string text = "restart must not re-push this";
        WriteTranscript(session, UserLine(text, ts));

        // A watermark file left behind by an EARLIER Director process - hand-written here with keys
        // this process never computed, which is exactly what a real restart reads off disk.
        var scope = SessionHistoryReader.ResolveTranscriptPath(session)!;
        var key = $"{ts:O}|{(int)ConversationRole.User}|{HashOfRestartMessage}";
        File.WriteAllText(
            Path.Combine(_root, "prompt-ingest-state.json"),
            JsonSerializer.Serialize(new Dictionary<string, HashSet<string>> { [scope] = new() { key } }));

        // Constructing the ingestor loads that file: this IS the restart.
        using var ingestor = NewIngestor();
        await ingestor.IngestAsync(session);

        // The message was already handed to the Gateway before the restart. Pushing it again means the
        // Gateway's prompt log - which appends blindly and never dedupes - grows a duplicate copy of the
        // whole conversation on every single Director restart.
        Assert.Empty(Pushed);
    }

    [Fact]
    public void The_persisted_key_is_a_content_hash_that_any_process_computes_identically()
    {
        var ts = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        var state = new IngestState();
        state.MarkWritten("some-scope", ts, ConversationRole.Assistant, "an old reply", tsFromAgent: true);
        state.Save();

        var written = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(
            File.ReadAllText(Path.Combine(_root, "prompt-ingest-state.json")))!;

        // The on-disk format is a contract with every FUTURE process that reads this file, so the key
        // must be a pure function of the message's content. A per-process value - string.GetHashCode()
        // being the obvious one - is unreadable by the process that comes next.
        var expected = $"{ts:O}|{(int)ConversationRole.Assistant}|{HashOfOldReply}";
        Assert.Equal(expected, Assert.Single(written["some-scope"]));
    }
}
