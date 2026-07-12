using CcDirector.Avalonia.Controls;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Pins the loopback URL construction for the fresh-device enrollment (epic #1069 A3). The same-machine
/// enroll MUST be dialed at 127.0.0.1 - the Gateway's guardrail 1 checks the caller's remote IP with
/// IPAddress.IsLoopback, so dialing the machine name or the tailnet address (which is what "This computer"
/// DISPLAYS) would arrive as a LAN/tailnet IP and 403, defeating the whole unblock. This test is the guard
/// against that regressing: whatever the pick's host, the enroll URL forces the literal 127.0.0.1 and keeps
/// the pick's port (the local Gateway's own port).
/// </summary>
public sealed class GatewayLoopbackEnrollUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7878", "http://127.0.0.1:7878")]
    [InlineData("http://SOREN_NORTH:7878", "http://127.0.0.1:7878")]
    [InlineData("http://soren-north:7930", "http://127.0.0.1:7930")]
    [InlineData("https://soren-north.taildb08ed.ts.net:7878", "http://127.0.0.1:7878")]
    public void ForcesLoopbackHost_AndKeepsThePickPort(string picked, string expected)
    {
        Assert.Equal(expected, GatewayConnectionPanel.BuildLoopbackEnrollUrl(picked));
    }

    [Fact]
    public void FallsBackToDefaultGatewayPort_ForAnUnparseableUrl()
    {
        Assert.Equal($"http://127.0.0.1:{EndpointProbe.DefaultGatewayPort}",
            GatewayConnectionPanel.BuildLoopbackEnrollUrl("not-a-url"));
    }
}
