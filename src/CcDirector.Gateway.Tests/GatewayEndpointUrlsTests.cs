using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the pure ordered-address assembly the Gateway publishes as <c>endpoint_urls</c> (issue #1233):
/// <see cref="GatewayHost.BuildOrderedEndpointUrls"/> orders the machine name, the Tailscale front door
/// (only when present), and the local network IP, applies an explicit operator override ahead of them all,
/// de-duplicates, and drops a malformed override so it can never make the account reject the whole
/// register/heartbeat (issue #334 validation contract). The I/O (probing Tailscale and the LAN) lives in the
/// Gateway host; this assembler takes the already-resolved pieces, so the priority logic is tested with no
/// real network.
/// </summary>
public sealed class GatewayEndpointUrlsTests
{
    private const string MachineName = "http://SOREN-NORTH:7878";
    private const string Tailscale = "https://soren-north.tail0123.ts.net:7878";
    private const string LanIp = "http://192.168.1.20:7878";

    // Tailscale present: three addresses in priority order (machine name, Tailscale, LAN IP).
    [Fact]
    public void BuildOrderedEndpointUrls_TailscalePresent_ThreeAddressesInPriorityOrder()
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(overrideUrl: null, MachineName, Tailscale, LanIp);

        Assert.Equal(new[] { MachineName, Tailscale, LanIp }, urls);
    }

    // Tailscale absent (null): two addresses (machine name, then LAN IP).
    [Fact]
    public void BuildOrderedEndpointUrls_NoTailscale_TwoAddresses()
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(overrideUrl: null, MachineName, tailscaleUrl: null, LanIp);

        Assert.Equal(new[] { MachineName, LanIp }, urls);
    }

    // No LAN IP either: just the machine name (always present).
    [Fact]
    public void BuildOrderedEndpointUrls_OnlyMachineName_WhenNeitherTailscaleNorLan()
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(overrideUrl: null, MachineName, tailscaleUrl: null, lanUrl: null);

        Assert.Equal(new[] { MachineName }, urls);
    }

    // A valid, non-loopback operator override ranks FIRST (it is the deliberate hand-set reachable address).
    [Fact]
    public void BuildOrderedEndpointUrls_ValidOverride_RanksFirst()
    {
        const string over = "https://gateway.example.com:8443";

        var urls = GatewayHost.BuildOrderedEndpointUrls(over, MachineName, Tailscale, LanIp);

        Assert.Equal(new[] { over, MachineName, Tailscale, LanIp }, urls);
    }

    // An override that equals a discovered address is not duplicated (case-insensitive).
    [Fact]
    public void BuildOrderedEndpointUrls_OverrideEqualsMachineName_NoDuplicate()
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls("HTTP://SOREN-NORTH:7878", MachineName, Tailscale, LanIp);

        Assert.Equal(new[] { "HTTP://SOREN-NORTH:7878", Tailscale, LanIp }, urls);
    }

    // A loopback override is never advertised (it is a lie to every remote caller).
    [Theory]
    [InlineData("http://127.0.0.1:7878")]
    [InlineData("http://localhost:7878")]
    public void BuildOrderedEndpointUrls_LoopbackOverride_Dropped(string loopback)
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(loopback, MachineName, Tailscale, LanIp);

        Assert.Equal(new[] { MachineName, Tailscale, LanIp }, urls);
    }

    // A malformed / non-http(s) override is dropped so it can never 400 the whole request (issue #334).
    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host:21")]
    [InlineData("gateway.example.com")] // no scheme -> not an absolute http(s) URL
    public void BuildOrderedEndpointUrls_MalformedOverride_Dropped(string bad)
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(bad, MachineName, Tailscale, LanIp);

        Assert.Equal(new[] { MachineName, Tailscale, LanIp }, urls);
    }

    // Blank tailscale/LAN entries are skipped (never a blank on the wire).
    [Fact]
    public void BuildOrderedEndpointUrls_BlankDiscoveredEntries_Skipped()
    {
        var urls = GatewayHost.BuildOrderedEndpointUrls(overrideUrl: null, MachineName, tailscaleUrl: "   ", lanUrl: "");

        Assert.Equal(new[] { MachineName }, urls);
    }

    [Theory]
    [InlineData("http://host:7878", true)]
    [InlineData("https://host.tailnet.ts.net:7878", true)]
    [InlineData("https://192.168.1.20:7878", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("host:7878", false)]          // no scheme
    [InlineData("ftp://host:21", false)]      // not http(s)
    [InlineData("ws://host:7878", false)]     // not http(s)
    public void IsPublishableHttpUrl_ClassifiesCorrectly(string? url, bool expected)
    {
        Assert.Equal(expected, GatewayHost.IsPublishableHttpUrl(url));
    }

    [Fact]
    public void IsPublishableHttpUrl_TooLong_False()
    {
        var tooLong = "http://" + new string('a', 200) + ":7878"; // > 200 chars

        Assert.False(GatewayHost.IsPublishableHttpUrl(tooLong));
    }
}
