using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

public class ClaudeHookEventParserTests
{
    [Fact]
    public void Parse_MappedCamelCaseShape_ReadsAllFields()
    {
        // The shape the Windows PowerShell hook script builds.
        var body = """
            {"claudeSessionId":"abc-123","transcriptPath":"C:\\t\\s.jsonl","hookEvent":"SessionStart","source":"clear"}
            """;

        var req = ClaudeHookEventParser.Parse(body);

        Assert.NotNull(req);
        Assert.Equal("abc-123", req!.ClaudeSessionId);
        Assert.Equal(@"C:\t\s.jsonl", req.TranscriptPath);
        Assert.Equal("SessionStart", req.HookEvent);
        Assert.Equal("clear", req.Source);
    }

    [Fact]
    public void Parse_RawClaudeEventShape_ReadsAllFields()
    {
        // Claude Code's raw hook event JSON, forwarded verbatim by the macOS/Linux shell hook.
        var body = """
            {"session_id":"def-456","transcript_path":"/Users/x/.claude/projects/p/def-456.jsonl","hook_event_name":"SessionStart","source":"compact","cwd":"/Users/x/repo"}
            """;

        var req = ClaudeHookEventParser.Parse(body);

        Assert.NotNull(req);
        Assert.Equal("def-456", req!.ClaudeSessionId);
        Assert.Equal("/Users/x/.claude/projects/p/def-456.jsonl", req.TranscriptPath);
        Assert.Equal("SessionStart", req.HookEvent);
        Assert.Equal("compact", req.Source);
    }

    [Fact]
    public void Parse_MappedShapeWins_WhenBothShapesPresent()
    {
        var body = """
            {"claudeSessionId":"mapped","session_id":"raw"}
            """;

        var req = ClaudeHookEventParser.Parse(body);

        Assert.NotNull(req);
        Assert.Equal("mapped", req!.ClaudeSessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void Parse_InvalidOrNonObjectBody_ReturnsNull(string body)
    {
        Assert.Null(ClaudeHookEventParser.Parse(body));
    }

    [Fact]
    public void Parse_ObjectWithoutKnownFields_ReturnsRequestWithNulls()
    {
        var req = ClaudeHookEventParser.Parse("""{"unrelated":true}""");

        Assert.NotNull(req);
        Assert.Null(req!.ClaudeSessionId);
        Assert.Null(req.TranscriptPath);
        Assert.Null(req.HookEvent);
        Assert.Null(req.Source);
    }

    [Fact]
    public void Parse_NonStringValues_AreIgnoredNotThrown()
    {
        var req = ClaudeHookEventParser.Parse("""{"session_id":42,"transcript_path":null}""");

        Assert.NotNull(req);
        Assert.Null(req!.ClaudeSessionId);
        Assert.Null(req.TranscriptPath);
    }
}
