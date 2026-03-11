using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class AgentTemplateTests
{
    [Fact]
    public void ToClaudeOptions_AllFields_MapsCorrectly()
    {
        var template = new AgentTemplate
        {
            Model = "opus",
            FallbackModel = "sonnet",
            MaxTurns = 10,
            MaxBudgetUsd = 5.00m,
            SystemPrompt = "You are a reviewer.",
            AppendSystemPrompt = "Be thorough.",
            PermissionMode = "plan",
            SkipPermissions = true,
            Tools = "Read,Write",
            AllowedTools = "Bash",
            DisallowedTools = "Edit",
            McpConfigPath = "/tmp/mcp.json",
        };

        var options = template.ToClaudeOptions();

        Assert.Equal("opus", options.Model);
        Assert.Equal("sonnet", options.FallbackModel);
        Assert.Equal(10, options.MaxTurns);
        Assert.Equal(5.00m, options.MaxBudgetUsd);
        Assert.Equal("You are a reviewer.", options.SystemPrompt);
        Assert.Equal("Be thorough.", options.AppendSystemPrompt);
        Assert.Equal("plan", options.PermissionMode);
        Assert.True(options.SkipPermissions);
        Assert.Equal("Read,Write", options.Tools);
        Assert.Equal("Bash", options.AllowedTools);
        Assert.Equal("Edit", options.DisallowedTools);
        Assert.Equal("/tmp/mcp.json", options.McpConfig);
    }

    [Fact]
    public void ToClaudeOptions_NullFields_RemainsNull()
    {
        var template = new AgentTemplate();

        var options = template.ToClaudeOptions();

        Assert.Null(options.Model);
        Assert.Null(options.FallbackModel);
        Assert.Null(options.MaxTurns);
        Assert.Null(options.MaxBudgetUsd);
        Assert.Null(options.SystemPrompt);
        Assert.Null(options.AppendSystemPrompt);
        Assert.Null(options.PermissionMode);
        Assert.False(options.SkipPermissions);
        Assert.Null(options.Tools);
        Assert.Null(options.AllowedTools);
        Assert.Null(options.DisallowedTools);
        Assert.Null(options.McpConfig);
    }

    [Fact]
    public void BuildCliArgs_WithModel_IncludesModelFlag()
    {
        var template = new AgentTemplate { Model = "opus" };

        var args = template.BuildCliArgs();

        Assert.Contains("--model opus", args);
    }

    [Fact]
    public void BuildCliArgs_WithAllFields_IncludesAllFlags()
    {
        var template = new AgentTemplate
        {
            Model = "opus",
            FallbackModel = "sonnet",
            MaxTurns = 5,
            MaxBudgetUsd = 2.50m,
            SystemPrompt = "Be helpful",
            AppendSystemPrompt = "Be safe",
            SkipPermissions = true,
            PermissionMode = "plan",
            Tools = "Read",
            AllowedTools = "Bash",
            DisallowedTools = "Write",
            McpConfigPath = "/config/mcp.json",
        };

        var args = template.BuildCliArgs();

        Assert.Contains("--model opus", args);
        Assert.Contains("--fallback-model sonnet", args);
        Assert.Contains("--max-turns 5", args);
        Assert.Contains("--max-budget-usd 2.5", args);
        Assert.Contains("--system-prompt", args);
        Assert.Contains("--append-system-prompt", args);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("--permission-mode plan", args);
        Assert.Contains("--tools", args);
        Assert.Contains("--allowedTools", args);
        Assert.Contains("--disallowedTools", args);
        Assert.Contains("--mcp-config", args);
    }

    [Fact]
    public void BuildCliArgs_Empty_ReturnsEmpty()
    {
        var template = new AgentTemplate();

        var args = template.BuildCliArgs();

        Assert.Equal("", args);
    }
}
