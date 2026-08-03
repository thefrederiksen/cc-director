using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeArgBuilderTests
{
    [Fact]
    public void BuildChatArgs_NullOptions_ReturnsBaseArgs()
    {
        var args = ClaudeArgBuilder.BuildChatArgs(null);

        Assert.Equal("-p --output-format json", args);
    }

    [Fact]
    public void BuildChatArgs_WithModel_IncludesModelFlag()
    {
        var options = new ClaudeOptions { Model = "haiku" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--model haiku", args);
        Assert.StartsWith("-p --output-format json", args);
    }

    [Fact]
    public void BuildChatArgs_WithMaxTurns_IncludesMaxTurnsFlag()
    {
        var options = new ClaudeOptions { MaxTurns = 3 };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--max-turns 3", args);
    }

    [Fact]
    public void BuildChatArgs_WithMaxBudget_IncludesBudgetFlag()
    {
        var options = new ClaudeOptions { MaxBudgetUsd = 0.01m };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--max-budget-usd 0.01", args);
    }

    [Fact]
    public void BuildChatArgs_SkipPermissions_IncludesFlag()
    {
        var options = new ClaudeOptions { SkipPermissions = true };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void BuildChatArgs_WithSystemPrompt_EscapesQuotes()
    {
        var options = new ClaudeOptions { SystemPrompt = "You are a \"helpful\" bot." };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--system-prompt \"You are a \\\"helpful\\\" bot.\"", args);
    }

    [Fact]
    public void BuildChatArgs_WithAppendSystemPrompt_IncludesFlag()
    {
        var options = new ClaudeOptions { AppendSystemPrompt = "Always be concise." };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--append-system-prompt \"Always be concise.\"", args);
    }

    [Fact]
    public void BuildChatArgs_WithTools_IncludesQuotedFlag()
    {
        var options = new ClaudeOptions { Tools = "Read,Bash" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--tools \"Read,Bash\"", args);
    }

    [Fact]
    public void BuildChatArgs_WithAllowedTools_IncludesFlag()
    {
        var options = new ClaudeOptions { AllowedTools = "Bash(git *)" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--allowedTools \"Bash(git *)\"", args);
    }

    [Fact]
    public void BuildChatArgs_WithSessionId_IncludesFlag()
    {
        var options = new ClaudeOptions { SessionId = "abc-123" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--session-id abc-123", args);
    }

    [Fact]
    public void BuildChatArgs_Continue_IncludesFlag()
    {
        var options = new ClaudeOptions { Continue = true };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--continue", args);
    }

    [Fact]
    public void BuildChatArgs_NoSessionPersistence_IncludesFlag()
    {
        var options = new ClaudeOptions { NoSessionPersistence = true };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--no-session-persistence", args);
    }

    [Fact]
    public void BuildChatArgs_WithAddDirs_IncludesMultipleFlags()
    {
        var options = new ClaudeOptions { AddDirs = ["../Core", "../Wpf"] };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--add-dir \"../Core\"", args);
        Assert.Contains("--add-dir \"../Wpf\"", args);
    }

    [Fact]
    public void BuildChatArgs_AllOptions_IncludesAllFlags()
    {
        var options = new ClaudeOptions
        {
            Model = "sonnet",
            FallbackModel = "haiku",
            MaxTurns = 5,
            MaxBudgetUsd = 0.50m,
            SkipPermissions = true,
            NoSessionPersistence = true,
        };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--model sonnet", args);
        Assert.Contains("--fallback-model haiku", args);
        Assert.Contains("--max-turns 5", args);
        Assert.Contains("--max-budget-usd 0.50", args);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [Fact]
    public void BuildStreamArgs_NullOptions_ReturnsBaseArgs()
    {
        var args = ClaudeArgBuilder.BuildStreamArgs(null);

        Assert.Equal("-p --output-format stream-json --verbose", args);
    }

    [Fact]
    public void BuildStreamArgs_WithModel_IncludesModelFlag()
    {
        var options = new ClaudeOptions { Model = "haiku" };

        var args = ClaudeArgBuilder.BuildStreamArgs(options);

        Assert.StartsWith("-p --output-format stream-json --verbose", args);
        Assert.Contains("--model haiku", args);
    }

    [Fact]
    public void BuildResumeArgs_IncludesSessionId()
    {
        var args = ClaudeArgBuilder.BuildResumeArgs("abc-123-def", null);

        Assert.Contains("--resume abc-123-def", args);
        Assert.StartsWith("-p --output-format json", args);
    }

    [Fact]
    public void BuildStructuredArgs_IncludesJsonSchema()
    {
        var args = ClaudeArgBuilder.BuildStructuredArgs(typeof(TestStructuredType), null);

        Assert.Contains("--json-schema", args);
        Assert.Contains("--output-format json", args);
        // Schema should contain escaped property names
        Assert.Contains("Answer", args);
        Assert.Contains("Score", args);
    }

    [Fact]
    public void EscapeJsonForWindowsArg_EscapesDoubleQuotes()
    {
        var json = "{\"type\":\"object\"}";

        var escaped = ClaudeArgBuilder.EscapeJsonForWindowsArg(json);

        Assert.Equal("{\\\"type\\\":\\\"object\\\"}", escaped);
    }

    [Fact]
    public void GenerateJsonSchema_ProducesValidSchema()
    {
        var schema = ClaudeArgBuilder.GenerateJsonSchema(typeof(TestStructuredType));

        Assert.Contains("object", schema);
        Assert.Contains("Answer", schema);
        Assert.Contains("Score", schema);
        Assert.Contains("string", schema);
        Assert.Contains("integer", schema);
    }

    [Fact]
    public void BuildChatArgs_WithPermissionMode_IncludesFlag()
    {
        var options = new ClaudeOptions { PermissionMode = "plan" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--permission-mode plan", args);
    }

    [Fact]
    public void BuildChatArgs_WithMcpConfig_IncludesFlag()
    {
        var options = new ClaudeOptions { McpConfig = "mcp.json" };

        var args = ClaudeArgBuilder.BuildChatArgs(options);

        Assert.Contains("--mcp-config \"mcp.json\"", args);
    }

    private sealed class TestStructuredType
    {
        public string Answer { get; set; } = "";
        public int Score { get; set; }
    }
}
