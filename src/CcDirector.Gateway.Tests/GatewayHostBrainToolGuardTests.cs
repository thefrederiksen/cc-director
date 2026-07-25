using System.Net.Sockets;
using CcDirector.Core.Agents;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A warm brain configured to a non-hostable agent tool must be rejected when the Gateway host is
/// CONSTRUCTED - loudly and early - not silently deferred to the brain's first spawn where it would
/// fail deep in StartAsync. The host validates the brain tool at the top of its constructor, before it
/// opens any resource, so a bad brain tool throws from the constructor itself.
/// </summary>
public sealed class GatewayHostBrainToolGuardTests
{
    private string InstancesDir() => Path.Combine(Path.GetTempPath(), "cc-braintool-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    public void Construction_WithNonHostableBrainTool_ThrowsAtConstruction(AgentKind tool)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: InstancesDir(), brainTool: tool));
        Assert.Contains("cannot be hosted", ex.Message);
    }

    [Fact]
    public async Task Construction_WithClaudeCodeBrainTool_Succeeds()
    {
        var dir = InstancesDir();
        await using var gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: dir, brainTool: AgentKind.ClaudeCode);

        Assert.Equal(AgentKind.ClaudeCode, gateway.BrainTool);

        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

}
