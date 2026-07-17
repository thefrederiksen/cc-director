using System.Net;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Pins the address ordering that makes a gateway named by a local (Bonjour/mDNS) computer name
/// dialable: macOS answers a <c>.local</c> lookup with the link-local IPv6 address first, and the
/// default sequential connect hangs on it. <see cref="GatewayHttp.OrderForDialing"/> must therefore
/// put IPv4 first, globally routable IPv6 next, and link-local IPv6 last.
/// </summary>
public class GatewayHttpTests
{
    private static readonly IPAddress Ipv4 = IPAddress.Parse("192.168.1.18");
    private static readonly IPAddress Ipv4Second = IPAddress.Parse("10.0.0.7");
    private static readonly IPAddress Ipv6Global = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress Ipv6LinkLocal = IPAddress.Parse("fe80::dd8a:282e:87c1:8f4b");

    [Fact]
    public void OrderForDialing_LinkLocalFirstFromResolver_MovesIpv4First()
    {
        // The exact shape macOS getaddrinfo returns for a .local name.
        var resolved = new[] { Ipv6LinkLocal, Ipv4 };

        var ordered = GatewayHttp.OrderForDialing(resolved);

        Assert.Equal(new[] { Ipv4, Ipv6LinkLocal }, ordered);
    }

    [Fact]
    public void OrderForDialing_AllThreeKinds_OrdersIpv4ThenGlobalIpv6ThenLinkLocal()
    {
        var resolved = new[] { Ipv6LinkLocal, Ipv6Global, Ipv4 };

        var ordered = GatewayHttp.OrderForDialing(resolved);

        Assert.Equal(new[] { Ipv4, Ipv6Global, Ipv6LinkLocal }, ordered);
    }

    [Fact]
    public void OrderForDialing_SameRank_KeepsResolverOrder()
    {
        var resolved = new[] { Ipv4Second, Ipv4 };

        var ordered = GatewayHttp.OrderForDialing(resolved);

        Assert.Equal(new[] { Ipv4Second, Ipv4 }, ordered);
    }

    [Fact]
    public void OrderForDialing_OnlyLinkLocal_StillReturnsIt()
    {
        // A link-local-only answer is a last resort, not a dead end.
        var ordered = GatewayHttp.OrderForDialing(new[] { Ipv6LinkLocal });

        Assert.Equal(new[] { Ipv6LinkLocal }, ordered);
    }

    [Fact]
    public void OrderForDialing_Loopback_IsUnaffected()
    {
        var resolved = new[] { IPAddress.IPv6Loopback, IPAddress.Loopback };

        var ordered = GatewayHttp.OrderForDialing(resolved);

        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, ordered);
    }

    [Fact]
    public void OrderForDialing_Empty_ReturnsEmpty()
    {
        Assert.Empty(GatewayHttp.OrderForDialing(Array.Empty<IPAddress>()));
    }
}
