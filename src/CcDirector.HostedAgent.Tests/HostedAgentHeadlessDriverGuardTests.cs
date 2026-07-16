using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.HostedAgent.Tests;

/// <summary>
/// The warm brain and every headless-hosted agent run through <see cref="HostedAgent"/>, which only
/// accepts the Claude Code kind - <see cref="HostedAgent.For"/> throws for every other kind. That is
/// what keeps the throw-only drivers (Cursor, Pi, and the generic driver), whose ResolveExecutable is
/// deliberately not implemented for headless hosting, off the warm-brain path entirely: they can never
/// be constructed there, so their unimplemented resolvers are unreachable. Only ClaudeDriver, routed
/// through the shared platform-aware ExecutableResolver, resolves an agent on this path.
/// </summary>
public sealed class HostedAgentHeadlessDriverGuardTests
{
    private static HostedAgentOptions Options() => new() { WorkingDirectory = Path.GetTempPath() };

    [Theory]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Gemini)]
    public void For_NonClaudeKind_Throws_SoNoThrowDriverIsReachableHeadless(AgentKind kind)
    {
        Assert.Throws<NotSupportedException>(() => HostedAgent.For(kind, Options()));
    }

    [Fact]
    public void For_ClaudeCode_IsTheOnlyHostedKind()
    {
        using var agent = HostedAgent.For(AgentKind.ClaudeCode, Options());
        Assert.NotNull(agent);
    }
}
