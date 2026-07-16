using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The warm brain can only be hosted by a driver that satisfies the hosted-agent contract (a
/// preassigned session id and transcript reads); today that is Claude Code alone. EnsureHostable is
/// the up-front gate the Gateway host uses at construction, so a brain tool nobody can host fails
/// loudly and early rather than silently deferring to a spawn-time failure.
/// </summary>
public sealed class BrainToolConfigEnsureHostableTests
{
    [Fact]
    public void EnsureHostable_ClaudeCode_ReturnsIt()
    {
        Assert.Equal(AgentKind.ClaudeCode, BrainToolConfig.EnsureHostable(AgentKind.ClaudeCode));
    }

    [Theory]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Gemini)]
    public void EnsureHostable_NonHostableTool_Throws(AgentKind tool)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BrainToolConfig.EnsureHostable(tool));
        Assert.Contains("cannot be hosted", ex.Message);
        Assert.Contains(tool.ToString(), ex.Message);
    }
}
