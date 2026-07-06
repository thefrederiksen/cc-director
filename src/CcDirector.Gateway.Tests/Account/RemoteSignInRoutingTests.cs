using System.Net;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Unit tests for the remote-vs-same-machine sign-in routing decision and the reachable front-door callback
/// URL builder (epic #1069, issue #1080). These are the two pure choices that make the credential-free
/// sign-in front door remote-capable, so they are proven directly here without a running host.
/// </summary>
public sealed class RemoteSignInRoutingTests
{
    [Fact]
    public void IsRemoteRequest_LoopbackIPv4_IsSameMachine()
    {
        Assert.False(RemoteSignInRouting.IsRemoteRequest(IPAddress.Loopback));
    }

    [Fact]
    public void IsRemoteRequest_LoopbackIPv6_IsSameMachine()
    {
        Assert.False(RemoteSignInRouting.IsRemoteRequest(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void IsRemoteRequest_NullAddress_IsTreatedAsSameMachine()
    {
        // Unknown source -> conservative host-local path, never a remote we cannot address.
        Assert.False(RemoteSignInRouting.IsRemoteRequest(null));
    }

    [Fact]
    public void IsRemoteRequest_TailnetAddress_IsRemote()
    {
        // A tailnet CGNAT-range client address (what a phone/laptop presents over Tailscale Serve).
        Assert.True(RemoteSignInRouting.IsRemoteRequest(IPAddress.Parse("100.86.144.11")));
    }

    [Fact]
    public void BuildFrontDoorCallback_UsesRequestSchemeAndHost_OnTheCallbackPath()
    {
        var callback = RemoteSignInRouting.BuildFrontDoorCallback("https", "gw.example-tailnet.ts.net");

        Assert.Equal("https", callback.Scheme);
        Assert.Equal("gw.example-tailnet.ts.net", callback.Host);
        Assert.Equal(RemoteSignInRouting.CallbackPath, callback.AbsolutePath);
    }

    [Fact]
    public void BuildFrontDoorCallback_MissingHost_ThrowsRatherThanFallingBackToLoopback()
    {
        // No-fallback rule: no reachable host -> surface the error, never silently degrade to loopback.
        Assert.Throws<InvalidOperationException>(() => RemoteSignInRouting.BuildFrontDoorCallback("https", ""));
    }

    [Fact]
    public void BuildFrontDoorCallback_MissingScheme_Throws()
    {
        Assert.Throws<ArgumentException>(() => RemoteSignInRouting.BuildFrontDoorCallback("", "gw.example-tailnet.ts.net"));
    }

    [Fact]
    public void BuildRemoteSignInUrl_CarriesTheFrontDoorCallbackAsRedirectUri_NotLoopback()
    {
        var url = RemoteSignInRouting.BuildRemoteSignInUrl("https", "gw.example-tailnet.ts.net");

        // Acceptance criterion 3: the redirect_uri the cloud sign-in page is sent to is the Gateway's
        // reachable front-door address, NOT a 127.0.0.1 loopback URL.
        var expectedCallback = Uri.EscapeDataString($"https://gw.example-tailnet.ts.net{RemoteSignInRouting.CallbackPath}");
        Assert.Contains($"redirect_uri={expectedCallback}", url, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", url, StringComparison.Ordinal);
        Assert.StartsWith(FirstRunLoginCoordinator.ResolveSignInBaseUrl(), url, StringComparison.Ordinal);
    }
}
