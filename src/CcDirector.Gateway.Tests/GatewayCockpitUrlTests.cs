using System;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="GatewayCockpitUrl"/> - the ONE server-side resolver for the public cockpit
/// base URL that <c>GET /cockpit</c>, the <c>CockpitUrl</c> on <c>GET /gateway/about</c>, and (through
/// <c>/cockpit</c>) the desktop Learn button all hand to the dumb client (CLAUDE.md rule 7).
///
/// These pin the two-mode contract on the pure overload, deterministically and without touching the real
/// environment or shelling tailscale:
///  - SELF-HOST: byte-identical to before this resolver existed - the tailnet front-door base passes
///    through untouched, and null (Tailscale down) stays null.
///  - HOSTED: the configured public URL, normalized to no trailing slash; and NO fallback - a hosted
///    Gateway with the URL unset FAILS LOUD rather than serving a null or a guess.
/// </summary>
public class GatewayCockpitUrlTests
{
    // ---- SELF-HOST: byte-identical to today ------------------------------------------------------

    [Fact]
    public void SelfHost_FrontDoorPresent_PassesFrontDoorThroughUnchanged()
    {
        // The self-host branch must return EXACTLY the tailnet front-door base it was handed - the same
        // string the endpoints emitted before the resolver existed. Reverting the hosted gate to always
        // return the front door would keep this green (it is the self-host control), which is the point:
        // this test asserts the self-host path is untouched.
        var result = GatewayCockpitUrl.ResolveBase(isHosted: false,
            hostedConfiguredUrl: null, selfHostFrontDoor: "https://machine-a.tail0123.ts.net");

        Assert.Equal("https://machine-a.tail0123.ts.net", result);
        // The call sites append "/", so the emitted URL is the historic value, unchanged.
        Assert.Equal("https://machine-a.tail0123.ts.net/", result + "/");
    }

    [Fact]
    public void SelfHost_FrontDoorNull_ReturnsNull()
    {
        // Tailscale down self-hosted: null in, null out - the caller surfaces "no remote URL", exactly as
        // before. No fabricated localhost substitute.
        var result = GatewayCockpitUrl.ResolveBase(isHosted: false,
            hostedConfiguredUrl: null, selfHostFrontDoor: null);

        Assert.Null(result);
    }

    [Fact]
    public void SelfHost_IgnoresAnyConfiguredHostedUrl()
    {
        // The hosted env var must have NO effect when not hosted - the gate is the hosted signal, never the
        // presence of the variable. A stray CC_GATEWAY_PUBLIC_COCKPIT_URL on a self-host box changes nothing.
        var result = GatewayCockpitUrl.ResolveBase(isHosted: false,
            hostedConfiguredUrl: "https://cockpit.devthrottle.com", selfHostFrontDoor: "https://host.ts.net");

        Assert.Equal("https://host.ts.net", result);
    }

    // ---- HOSTED: the configured public URL, or fail loud -----------------------------------------

    [Fact]
    public void Hosted_ConfiguredUrl_ReturnsIt()
    {
        // The headline P1 behaviour: hosted returns the configured public cockpit URL, NOT the (absent)
        // tailnet front door. Reverting the hosted branch to the front-door path turns this red with the
        // reported symptom (a null / tailscale URL where the public URL should be).
        var result = GatewayCockpitUrl.ResolveBase(isHosted: true,
            hostedConfiguredUrl: "https://cockpit.devthrottle.com", selfHostFrontDoor: null);

        Assert.Equal("https://cockpit.devthrottle.com", result);
        Assert.Equal("https://cockpit.devthrottle.com/", result + "/");
    }

    [Fact]
    public void Hosted_ConfiguredUrlWithTrailingSlashOrSpace_IsNormalized()
    {
        // The base carries no trailing slash so the call sites' own "/" never doubles into "//".
        var result = GatewayCockpitUrl.ResolveBase(isHosted: true,
            hostedConfiguredUrl: "  https://cockpit.devthrottle.com/  ", selfHostFrontDoor: null);

        Assert.Equal("https://cockpit.devthrottle.com", result);
        Assert.Equal("https://cockpit.devthrottle.com/", result + "/");
    }

    [Fact]
    public void Hosted_UrlUnset_FailsLoud()
    {
        // NO fallback: a hosted Gateway with the public URL unset is a deploy misconfiguration. It throws
        // rather than serving null (which the client would render as "Tailscale unavailable") or a guess.
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayCockpitUrl.ResolveBase(
            isHosted: true, hostedConfiguredUrl: null, selfHostFrontDoor: "https://host.ts.net"));

        Assert.Contains(GatewayCockpitUrl.PublicCockpitUrlEnvVar, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hosted_UrlBlank_FailsLoud(string blank)
    {
        // Blank is as misconfigured as unset - an empty or whitespace value is not a URL.
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayCockpitUrl.ResolveBase(
            isHosted: true, hostedConfiguredUrl: blank, selfHostFrontDoor: null));

        Assert.Contains(GatewayCockpitUrl.PublicCockpitUrlEnvVar, ex.Message);
    }
}
