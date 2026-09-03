using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// The Pi launch contract (issue #2670): every launch names its session with <c>--session-id</c>, so the
/// transcript file is known from birth instead of guessed from the newest file in the repo.
/// </summary>
public class PiAgentTests
{
    [Fact]
    public void SupportsPreassignedSessionId_IsTrue()
    {
        var agent = new PiAgent(new AgentOptions());
        Assert.True(agent.SupportsPreassignedSessionId);
    }

    [Fact]
    public void Plugin_LaunchMetadata_DeclaresPreassignedSessionId()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.Pi);
        Assert.True(plugin.Launch.SupportsPreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_NewSession_EmitsMintedSessionId()
    {
        var agent = new PiAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.NotNull(spec.PreassignedSessionId);
        Assert.True(Guid.TryParse(spec.PreassignedSessionId, out _));
        Assert.Equal($"--session-id {spec.PreassignedSessionId}", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_TwoNewSessions_GetDifferentIds()
    {
        var agent = new PiAgent(new AgentOptions());

        var a = agent.BuildLaunchSpec(null, null, false);
        var b = agent.BuildLaunchSpec(null, null, false);

        Assert.NotEqual(a.PreassignedSessionId, b.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_Resume_PassesTheSameIdAsSessionId()
    {
        // pi resumes a project session by id ("--session-id: use exact project session ID, creating it if
        // missing"), so a reopen carries the id it had, and the locator finds the same file.
        var agent = new PiAgent(new AgentOptions());
        const string id = "8be79bf8-7db0-46c2-b19e-73857c9a7159";

        var spec = agent.BuildLaunchSpec(userArgs: "--thinking high", resumeSessionId: id, studioMode: false);

        Assert.Equal(id, spec.PreassignedSessionId);
        Assert.Equal($"--thinking high --session-id {id}", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_UserArgs_KeptAheadOfTheSessionId()
    {
        var agent = new PiAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: "  --model gpt-5.5  ", resumeSessionId: null, studioMode: false);

        Assert.StartsWith("--model gpt-5.5 --session-id ", spec.Arguments);
    }
}
