using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Phone panel's scannable code (devthrottle_internal #1508) encodes the address the Cockpit was
/// REACHED on, because that is the address the person's phone has to reach too.
///
/// These pin the one case where that rule produces something worse than nothing: a Cockpit opened on
/// loopback. The code would render perfectly, scan perfectly, and then time out on the phone, leaving
/// the person debugging their network instead of their address - so the endpoint refuses and says so.
/// A loopback host that slipped through this check is invisible in every other test: the endpoint still
/// answers 200 with a valid PNG.
/// </summary>
public sealed class MobileQrEndpointTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]      // routing is case-insensitive, so the check has to be too
    [InlineData("127.0.0.1")]
    [InlineData("127.5.5.5")]      // the whole 127/8 block is loopback, not just .0.1
    [InlineData("::1")]
    [InlineData("[::1]")]          // as it arrives in a Host header
    public void IsLoopback_ForAnAddressOnlyThisMachineCanReach_IsTrue(string host)
    {
        Assert.True(MobileQrEndpoint.IsLoopback(host));
    }

    [Theory]
    [InlineData("devthrottle-gw.azurewebsites.net")]
    [InlineData("192.168.1.40")]                    // the ordinary self-host case: a phone on the same network
    [InlineData("soren-north")]                     // a bare machine name
    [InlineData("100.64.0.1")]                      // a Tailscale address
    [InlineData("")]
    public void IsLoopback_ForAnAddressAPhoneCanReach_IsFalse(string host)
    {
        Assert.False(MobileQrEndpoint.IsLoopback(host));
    }
}
