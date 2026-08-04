using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Remove-the-network-port mission ended the advertised inbound endpoint, and this pins the
/// registration's new shape: identity only, ALWAYS.
///
/// The issue #324 regression this file used to guard - "never claim a reachable endpoint you do not
/// have" - is now satisfied by construction: there is no endpoint resolution at all, no Tailscale
/// detection ladder, and no unreachable-reason to carry, because the Director listens on nothing and
/// reachability is the tunnel connection itself. What is worth pinning is that the request really
/// does advertise NOTHING (a reintroduced endpoint would re-open the door the mission closed) while
/// still carrying the identity the Gateway registers.
/// </summary>
public sealed class GatewayClientRegistrationRequestTests
{
    [Fact]
    public void BuildRegistrationRequest_AdvertisesNoEndpointAndNoReason()
    {
        var cfg = new GatewayConfig { Url = "http://127.0.0.1:1", Token = "" };
        using var client = new GatewayClient(cfg, Guid.NewGuid().ToString(), "9.9.9-test");

        var req = client.BuildRegistrationRequest();

        Assert.Equal("", req.TailnetEndpoint);
        Assert.Null(req.EndpointUnreachableReason);
    }

    [Fact]
    public void BuildRegistrationRequest_CarriesTheIdentityTheGatewayRegisters()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig { Url = "http://127.0.0.1:1", Token = "" };
        using var client = new GatewayClient(cfg, id, "9.9.9-test");

        var req = client.BuildRegistrationRequest();

        Assert.Equal(id, req.DirectorId);
        Assert.Equal(Environment.ProcessId, req.Pid);
        Assert.Equal("9.9.9-test", req.Version);
        Assert.Equal(Environment.MachineName, req.MachineName);
        Assert.NotEqual(default, req.StartedAt);
    }
}
