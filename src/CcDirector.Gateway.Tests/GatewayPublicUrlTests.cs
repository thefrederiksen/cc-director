using System;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="GatewayPublicUrl"/> - the ONE server-side resolver that turns the Gateway's
/// public base URL plus a surface PATH into the full URL handed to the dumb client (CLAUDE.md rule 7).
/// The Cockpit surface (<c>{base}/cockpit</c>) is wired at <c>GET /cockpit</c>, the <c>CockpitUrl</c> on
/// <c>GET /gateway/about</c>, and the <c>cockpit.url</c> on <c>GET /gateway/settings</c>. The mobile
/// surface (<c>{base}/mobile</c>) is DEFERRED to P3 (nothing wires it yet), but it is resolved by the
/// SAME rule and is pinned here so the resolver is one complete thing.
///
/// The two-mode contract, on the pure overload, deterministically and without touching the real
/// environment or shelling tailscale:
///  - SELF-HOST: the tailnet front-door base with the surface path appended; null (Tailscale down) stays
///    null. The base-resolution branch is byte-identical to before this resolver existed.
///  - HOSTED: the configured public base (normalized to no trailing slash) with the surface path
///    appended; and NO fallback - a hosted Gateway with the base unset FAILS LOUD, never a null or guess.
///
/// The pure overload cannot prove the LIVE wrapper (<see cref="GatewayPublicUrl.ResolveCockpit"/>, which
/// reads the real environment via <see cref="GatewayPublicUrl.Resolve(string)"/>) is actually connected to
/// it. The two "live wrapper" cases below drive <c>ResolveCockpit()</c> through the real env for BOTH modes,
/// so replacing it with a hardcoded constant reddens. Env vars are process-global; the whole assembly runs
/// sequentially (see TestParallelization.cs), and each case saves/restores in a finally.
///
/// The configured base here is a deliberately NON-production, clearly-fake host, so a hardcoded production
/// constant (https://gateway.devthrottle.com/...) does not coincidentally match the expectation.
/// </summary>
public class GatewayPublicUrlTests
{
    private const string FrontDoor = "https://machine-a.tail0123.ts.net";
    private const string PublicBase = "https://gw.test.invalid";

    // ---- SELF-HOST: front door + surface path, byte-identical base resolution --------------------

    [Theory]
    [InlineData(GatewayPublicUrl.CockpitPath, FrontDoor + "/cockpit")]
    [InlineData(GatewayPublicUrl.MobilePath, FrontDoor + "/mobile")]
    public void SelfHost_FrontDoorPresent_AppendsSurfacePath(string surfacePath, string expected)
    {
        // The self-host branch returns the tailnet front-door base with the surface path appended. Reverting
        // the hosted gate to always return the front door would keep this green (it is the self-host
        // control): this test asserts the self-host base resolution is untouched.
        var result = GatewayPublicUrl.Resolve(isHosted: false,
            hostedConfiguredBase: null, selfHostFrontDoor: FrontDoor, surfacePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(GatewayPublicUrl.CockpitPath)]
    [InlineData(GatewayPublicUrl.MobilePath)]
    public void SelfHost_FrontDoorNull_ReturnsNull(string surfacePath)
    {
        // Tailscale down self-hosted: null in, null out - the caller surfaces "no remote URL", exactly as
        // before. No fabricated localhost substitute.
        var result = GatewayPublicUrl.Resolve(isHosted: false,
            hostedConfiguredBase: null, selfHostFrontDoor: null, surfacePath);

        Assert.Null(result);
    }

    [Fact]
    public void SelfHost_IgnoresAnyConfiguredHostedBase()
    {
        // The hosted env var must have NO effect when not hosted - the gate is the hosted signal, never the
        // presence of the variable. A stray CC_GATEWAY_PUBLIC_URL on a self-host box changes nothing.
        var result = GatewayPublicUrl.Resolve(isHosted: false,
            hostedConfiguredBase: PublicBase, selfHostFrontDoor: FrontDoor, GatewayPublicUrl.CockpitPath);

        Assert.Equal(FrontDoor + "/cockpit", result);
    }

    // ---- HOSTED: configured base + surface path, or fail loud ------------------------------------

    [Theory]
    [InlineData(GatewayPublicUrl.CockpitPath, PublicBase + "/cockpit")]
    [InlineData(GatewayPublicUrl.MobilePath, PublicBase + "/mobile")]
    public void Hosted_ConfiguredBase_AppendsSurfacePath(string surfacePath, string expected)
    {
        // The headline P1 behaviour: hosted returns {configured base}/{surface}, NOT the (absent) tailnet
        // front door. Reverting the hosted branch to the front-door path turns this red with the reported
        // symptom (a null / tailscale URL where the public URL should be).
        var result = GatewayPublicUrl.Resolve(isHosted: true,
            hostedConfiguredBase: PublicBase, selfHostFrontDoor: null, surfacePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(GatewayPublicUrl.CockpitPath, PublicBase + "/cockpit")]
    [InlineData(GatewayPublicUrl.MobilePath, PublicBase + "/mobile")]
    public void Hosted_BaseWithTrailingSlashOrSpace_IsNormalized(string surfacePath, string expected)
    {
        // The base is trimmed of whitespace and any trailing slash so it never doubles into "//path".
        var result = GatewayPublicUrl.Resolve(isHosted: true,
            hostedConfiguredBase: "  " + PublicBase + "/  ", selfHostFrontDoor: null, surfacePath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Hosted_BaseUnset_FailsLoud()
    {
        // NO fallback: a hosted Gateway with the public base unset is a deploy misconfiguration. It throws
        // rather than serving null (which the client would render as "Tailscale unavailable") or a guess.
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayPublicUrl.Resolve(
            isHosted: true, hostedConfiguredBase: null, selfHostFrontDoor: FrontDoor, GatewayPublicUrl.CockpitPath));

        Assert.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hosted_BaseBlank_FailsLoud(string blank)
    {
        // Blank is as misconfigured as unset - an empty or whitespace value is not a URL.
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayPublicUrl.Resolve(
            isHosted: true, hostedConfiguredBase: blank, selfHostFrontDoor: null, GatewayPublicUrl.MobilePath));

        Assert.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar, ex.Message);
    }

    // ---- LIVE WRAPPER: ResolveCockpit() reads the real environment -------------------------------
    // These prove the wrapper is actually WIRED to the pure resolver (not stubbed or hardcoded). They drive
    // ResolveCockpit() through the real env for BOTH modes. Replacing ResolveCockpit() with a hardcoded
    // constant reddens the hosted case (it pins the exact fake-base value) and would break the self-host
    // case's independence from the hosted var. Assembly runs sequentially; env saved/restored in finally.

    [Fact]
    public void ResolveCockpit_LiveWrapper_Hosted_DerivesFromConfiguredBase()
    {
        WithEnv(hosted: "1", publicBase: PublicBase, () =>
        {
            // Deterministic: hosted needs no tailnet. The wrapper must return {configured base}/cockpit, the
            // NON-production value - so a hardcoded ResolveCockpit() constant (prod or otherwise) reddens.
            Assert.Equal(PublicBase + "/cockpit", GatewayPublicUrl.ResolveCockpit());
        });
    }

    [Fact]
    public void ResolveCockpit_LiveWrapper_SelfHost_IgnoresStrayHostedBase()
    {
        WithEnv(hosted: null, publicBase: PublicBase, () =>
        {
            // Not hosted: the wrapper takes the self-host branch, which reads the tailnet front door (null on
            // a build host with no tailnet) and NEVER the hosted env var. So the result is either null or a
            // {tailnet}/cockpit URL - but never the stray hosted base. This proves the wrapper routes through
            // Resolve()'s hosted gate rather than reading CC_GATEWAY_PUBLIC_URL directly, for both outcomes.
            var result = GatewayPublicUrl.ResolveCockpit();

            Assert.NotEqual(PublicBase + "/cockpit", result);
            if (result is not null)
                Assert.EndsWith("/cockpit", result);
        });
    }

    // ---- convenience wrappers append the right surface path --------------------------------------

    [Fact]
    public void ResolveCockpit_ResolveMobile_UseTheirSurfacePaths()
    {
        // ResolveCockpit()/ResolveMobile() are thin wrappers over Resolve(surfacePath) that read the live
        // environment. The surface constants they pass are what the pure tests above pin - so a swap of any
        // constant flips a case.
        Assert.Equal("/cockpit", GatewayPublicUrl.CockpitPath);
        Assert.Equal("/mobile", GatewayPublicUrl.MobilePath);
    }

    // Save/restore the two process-global env vars the resolver reads, then run body under the given values.
    private static void WithEnv(string? hosted, string? publicBase, Action body)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorBase = Environment.GetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted);
        Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, publicBase);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, priorBase);
        }
    }
}
