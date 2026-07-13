using System.Net;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The /diag/* network-diagnostics helpers (auto-network-switching mission). ClassifyClientIp is the
/// brains of the mobile Diagnostics page's route readout: it turns the IP the Gateway sees (after
/// X-Forwarded-For) into the label that tells a direct-LAN hit apart from a Tailscale relay. BuildPayload
/// backs the throughput test.
/// </summary>
public sealed class NetDiagTests
{
    [Theory]
    // Tailscale CGNAT 100.64.0.0/10 (the phone's tailnet IP through the ts.net front door).
    [InlineData("100.64.0.1", "tailscale")]
    [InlineData("100.100.100.100", "tailscale")]
    [InlineData("100.127.255.254", "tailscale")]
    // The 100.x addresses OUTSIDE the /10 are NOT Tailscale - they are ordinary public space.
    [InlineData("100.63.255.255", "other")]
    [InlineData("100.128.0.1", "other")]
    // RFC-1918 private LAN ranges (a direct hit on the home network).
    [InlineData("192.168.1.42", "lan")]
    [InlineData("10.0.0.5", "lan")]
    [InlineData("172.16.0.1", "lan")]
    [InlineData("172.31.255.254", "lan")]
    [InlineData("172.15.0.1", "other")] // just below the 172.16-31 block
    [InlineData("172.32.0.1", "other")] // just above it
    [InlineData("169.254.1.1", "lan")] // APIPA link-local
    // Loopback.
    [InlineData("127.0.0.1", "local")]
    [InlineData("::1", "local")]
    // Public / other.
    [InlineData("8.8.8.8", "other")]
    public void ClassifyClientIp_KnownRanges_AreNamed(string ip, string expected)
    {
        Assert.Equal(expected, NetDiag.ClassifyClientIp(IPAddress.Parse(ip)));
    }

    [Fact]
    public void ClassifyClientIp_Null_IsOther()
    {
        Assert.Equal("other", NetDiag.ClassifyClientIp(null));
    }

    [Fact]
    public void ClassifyClientIp_Ipv4MappedToIpv6_IsUnwrapped()
    {
        // A dual-stack Kestrel can surface an IPv4 client as ::ffff:192.168.1.42; it must still read as LAN.
        var mapped = IPAddress.Parse("192.168.1.42").MapToIPv6();
        Assert.Equal("lan", NetDiag.ClassifyClientIp(mapped));
    }

    [Fact]
    public void BuildPayload_ReturnsRequestedSize()
    {
        Assert.Equal(1024, NetDiag.BuildPayload(1024).Length);
        Assert.Empty(NetDiag.BuildPayload(0));
    }

    [Fact]
    public void BuildPayload_IsNotAllZeros()
    {
        // The payload must not be trivially compressible, or a throughput measurement over a compressing
        // transport would be inflated. A varied LCG fill has many distinct byte values.
        var data = NetDiag.BuildPayload(4096);
        var distinct = new HashSet<byte>(data);
        Assert.True(distinct.Count > 32, $"expected a varied fill, saw {distinct.Count} distinct byte values");
    }
}
