using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for the Director Learn button (#475). The Learn URL is NO LONGER composed on the client -
/// the Gateway hands it back as <c>CockpitInfoDto.LearnUrl</c> ({base}/learn) and <c>BtnLearn_Click</c>
/// opens it verbatim (CLAUDE.md rule 7), so the former <c>BuildLearnUrl</c> string-composer and its tests
/// are gone (a client-composed <c>Url + "/learn"</c> would yield the non-route {base}/cockpit/learn now
/// that Url is {base}/cockpit; the resolution is proven server-side in CockpitUrlEndpointTests /
/// GatewayPublicUrlTests). What remains testable here is the pure "could not reach the gateway" message
/// (reusing the existing Gateway-tray hint) for the not-reachable failure state.
/// </summary>
public class LearnButtonUrlTests
{
    [Fact]
    public void BuildGatewayUnreachableMessage_LoopbackDefault_UsesLocalGatewayTrayHint()
    {
        // Arrange: no gateway configured -> the resolver returns the loopback default.
        var baseUrl = CockpitUrlResolver.LocalhostDefault;

        // Act
        var message = MainWindow.BuildGatewayUnreachableMessage(baseUrl, "Connection refused");

        // Assert: explicit message naming the probed URL plus the local Gateway-tray hint.
        Assert.Contains(baseUrl, message);
        Assert.Contains("Connection refused", message);
        Assert.Contains("Is the Gateway tray app (devthrottle-gateway) running on this machine?", message);
    }

    [Fact]
    public void BuildGatewayUnreachableMessage_ConfiguredRemoteGateway_UsesTailnetReachabilityHint()
    {
        // Arrange: a configured remote gateway URL (not the loopback default).
        var baseUrl = "http://example-host.ts.net:7878";

        // Act
        var message = MainWindow.BuildGatewayUnreachableMessage(baseUrl, "Timeout");

        // Assert: the remote-reachability hint, not the local tray hint.
        Assert.Contains(baseUrl, message);
        Assert.Contains("Timeout", message);
        Assert.Contains("Is the Gateway running on that machine and reachable over your tailnet?", message);
    }
}
