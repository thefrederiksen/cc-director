using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Pins the normalized parse as a true SUPERSET of the transcript (issue #1561), so the other hand-rolled
/// walks of the same file can collapse onto it without losing what they read today.
///
/// Four fields the normalized shape used to drop, and one filter it used to apply silently:
/// - is_error      - without it a rebuilt Agent view cannot show a failed tool call
/// - isMeta        - WidgetBuilder skips these; including them would add cards the user never saw
/// - line number   - the incremental read and the tail both need an offset
/// - usage-only lines - an assistant line can carry token usage and no text; dropping it loses the turn
/// - isSidechain   - was FILTERED at parse; now flagged, because the Agent view shows these and a
///                   conversation replay does not. Only the consumer can decide.
///
/// The second half pins the facade's long-standing contract, which every existing caller depends on and
/// which must NOT change just because the parse now keeps more.
/// </summary>
public sealed class ClaudeTranscriptSupersetTests : IDisposable
{
    private readonly string _dir;

    public ClaudeTranscriptSupersetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-superset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Line(object o) => JsonSerializer.Serialize(o);

    private static string UserText(string text) => Line(new
    {
        type = "user",
        sessionId = "ctx-1",
        message = new { role = "user", content = new[] { new { type = "text", text } } },
    });

    // ===== the superset =====

    [Fact]
    public void A_failed_tool_result_keeps_its_error_flag()
    {
        var path = Write(Line(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "tool_result", tool_use_id = "t1", content = "boom", is_error = true } },
            },
        }));

        var part = Assert.Single(Assert.Single(ClaudeTranscriptReader.Read(path).Messages).Parts);

        Assert.Equal(ConversationPartKind.ToolResult, part.Kind);
        Assert.True(part.IsError);
    }

    [Fact]
    public void A_successful_tool_result_is_not_marked_as_an_error()
    {
        var path = Write(Line(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "tool_result", tool_use_id = "t1", content = "fine" } },
            },
        }));

        Assert.False(Assert.Single(Assert.Single(ClaudeTranscriptReader.Read(path).Messages).Parts).IsError);
    }

    [Fact]
    public void A_meta_line_is_flagged_rather_than_silently_kept_as_a_real_message()
    {
        var path = Write(Line(new
        {
            type = "user",
            isMeta = true,
            message = new { role = "user", content = new[] { new { type = "text", text = "injected by the agent" } } },
        }));

        var only = Assert.Single(ClaudeTranscriptReader.Read(path).Messages);

        // Kept (a consumer may want it) but marked, so a renderer can skip it as WidgetBuilder does.
        Assert.True(only.IsMeta);
    }

    [Fact]
    public void An_ordinary_line_is_not_meta_and_not_a_sidechain()
    {
        var only = Assert.Single(ClaudeTranscriptReader.Read(Write(UserText("hello"))).Messages);

        Assert.False(only.IsMeta);
        Assert.False(only.IsSidechain);
    }

    [Fact]
    public void A_sidechain_turn_is_kept_and_flagged_rather_than_dropped_at_parse()
    {
        var path = Write(
            UserText("main thread"),
            Line(new
            {
                type = "assistant",
                isSidechain = true,
                message = new { role = "assistant", content = new[] { new { type = "text", text = "subagent working" } } },
            }));

        var messages = ClaudeTranscriptReader.Read(path).Messages;

        // Both present: the parse no longer decides for the consumer.
        Assert.Equal(2, messages.Count);
        Assert.False(messages[0].IsSidechain);
        Assert.True(messages[1].IsSidechain);
    }

    [Fact]
    public void An_assistant_line_carrying_only_usage_is_kept()
    {
        var path = Write(Line(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                model = "claude-opus-4-8",
                content = Array.Empty<object>(),
                usage = new { input_tokens = 100, output_tokens = 20 },
            },
        }));

        // No text at all, but it carries the turn's token usage - dropping it loses that turn from the
        // accounting, which is exactly why usage had to be parsed separately.
        var only = Assert.Single(ClaudeTranscriptReader.Read(path).Messages);
        Assert.Empty(only.Parts);
    }

    [Fact]
    public void A_line_with_neither_content_nor_usage_is_still_dropped()
    {
        var path = Write(Line(new { type = "assistant", message = new { role = "assistant", content = Array.Empty<object>() } }));

        Assert.Empty(ClaudeTranscriptReader.Read(path).Messages);
    }

    [Fact]
    public void Every_message_carries_its_line_number()
    {
        var path = Write(UserText("first"), UserText("second"), UserText("third"));

        var messages = ClaudeTranscriptReader.Read(path).Messages;

        Assert.Equal(new int?[] { 1, 2, 3 }, messages.Select(m => m.LineNumber));
    }

    [Fact]
    public void Line_numbers_count_blank_lines_so_they_match_the_real_file()
    {
        var path = Path.Combine(_dir, "blanks.jsonl");
        File.WriteAllLines(path, new[] { UserText("first"), "", UserText("third") });

        var messages = ClaudeTranscriptReader.Read(path).Messages;

        // An offset is only useful if it addresses the FILE, not the surviving messages.
        Assert.Equal(new int?[] { 1, 3 }, messages.Select(m => m.LineNumber));
    }

    // ===== the facade's contract must not change =====

    [Fact]
    public void MainThread_hides_sidechains_and_usage_only_lines()
    {
        var path = Write(
            UserText("main thread"),
            Line(new
            {
                type = "assistant",
                isSidechain = true,
                message = new { role = "assistant", content = new[] { new { type = "text", text = "subagent" } } },
            }),
            Line(new
            {
                type = "assistant",
                message = new { role = "assistant", content = Array.Empty<object>(), usage = new { input_tokens = 1 } },
            }));

        var main = ClaudeTranscriptReader.Read(path).MainThread;

        // What every existing caller of SessionHistoryReader.Read saw before this change, unchanged.
        Assert.Equal("main thread", Assert.Single(main.Messages).Parts[0].Text);
    }

    [Fact]
    public void MainThread_keeps_meta_lines_because_that_is_what_callers_saw_before()
    {
        var path = Write(Line(new
        {
            type = "user",
            isMeta = true,
            message = new { role = "user", content = new[] { new { type = "text", text = "meta with content" } } },
        }));

        // The old parser never read isMeta, so a meta line WITH content was a normal message. Flagging it
        // must not quietly start hiding it - only a renderer that already skipped meta should skip it.
        Assert.Single(ClaudeTranscriptReader.Read(path).MainThread.Messages);
    }

    [Fact]
    public void MainThread_of_an_all_sidechain_transcript_is_empty_not_null()
    {
        var path = Write(Line(new
        {
            type = "assistant",
            isSidechain = true,
            message = new { role = "assistant", content = new[] { new { type = "text", text = "only subagent" } } },
        }));

        Assert.Empty(ClaudeTranscriptReader.Read(path).MainThread.Messages);
    }

    [Fact]
    public void MainThread_returns_the_same_instance_when_nothing_needs_filtering()
    {
        var history = ClaudeTranscriptReader.Read(Write(UserText("a"), UserText("b")));

        // Cheap path: the common case allocates nothing.
        Assert.Same(history, history.MainThread);
    }
}
