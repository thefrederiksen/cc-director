using System.Net;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1870: on the hosted Gateway every ViewUrl was minted as <c>gw=http://...</c> on an HTTPS-only
/// host. The cause was proxy trust, not URL building - forwarded headers were accepted from loopback only,
/// which is right for the self-hosted Tailscale Serve front end but discards
/// <c>X-Forwarded-Proto: https</c> from the Azure App Service front end, which forwards from a non-loopback
/// platform address.
///
/// These tests pin both halves of that decision. The options tests state the policy; the two end-to-end
/// tests run a real <see cref="ForwardedHeadersMiddleware"/> and assert on the scheme a request handler
/// actually observes, because the options being set is not the same claim as the scheme surviving the
/// middleware.
/// </summary>
public class ForwardedHeadersPolicyTests
{
    private static ForwardedHeadersOptions Apply(bool isHosted)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersPolicy.Apply(options, isHosted);
        return options;
    }

    [Fact]
    public void SelfHosted_TrustsLoopbackAndNothingElse()
    {
        var o = Apply(isHosted: false);

        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, o.KnownProxies);
        Assert.Empty(o.KnownIPNetworks);
    }

    [Fact]
    public void Hosted_LeavesTheKnownProxySetEmptySoThePlatformFrontEndIsAccepted()
    {
        var o = Apply(isHosted: true);

        // An empty known-proxy AND known-network set is how ForwardedHeadersMiddleware is told to accept a
        // front end it cannot enumerate. A loopback entry here is the #1870 defect.
        Assert.Empty(o.KnownProxies);
        Assert.Empty(o.KnownIPNetworks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothDeployments_ReadProtoHostAndFor(bool isHosted)
    {
        var o = Apply(isHosted);

        Assert.True(o.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
        Assert.True(o.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.True(o.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
    }

    [Fact]
    public void Apply_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ForwardedHeadersPolicy.Apply(null!, isHosted: true));

    /// <summary>
    /// Run a request through a real ForwardedHeadersMiddleware from a NON-loopback address, exactly as the
    /// Azure App Service front end does, and assert on the scheme the handler observes. This is the test
    /// that actually reproduces #1870: with the hosted policy the handler sees "https"; with the self-host
    /// policy the same request is seen as "http", which is what every hosted ViewUrl was built from.
    /// </summary>
    private static async Task<string> ObservedSchemeAsync(bool isHosted, string frontEndAddress)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersPolicy.Apply(options, isHosted);

        var observed = "";
        var middleware = new ForwardedHeadersMiddleware(
            next: ctx => { observed = ctx.Request.Scheme; return Task.CompletedTask; },
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options));

        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";                       // what the front end forwards as
        context.Connection.RemoteIpAddress = IPAddress.Parse(frontEndAddress);
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = frontEndAddress;

        await middleware.Invoke(context);
        return observed;
    }

    [Fact]
    public async Task Hosted_HttpsFromANonLoopbackFrontEnd_IsSeenAsHttps()
    {
        // 169.254.x.x is the shape of an App Service platform forwarder: not loopback, not enumerable.
        Assert.Equal("https", await ObservedSchemeAsync(isHosted: true, frontEndAddress: "169.254.130.5"));
    }

    [Fact]
    public async Task SelfHosted_HttpsFromANonLoopbackSender_IsIgnored()
    {
        // The self-host guard that must NOT be weakened: an arbitrary sender cannot claim HTTPS.
        Assert.Equal("http", await ObservedSchemeAsync(isHosted: false, frontEndAddress: "169.254.130.5"));
    }
}
