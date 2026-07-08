using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The wingman (chat-completions) and text-to-speech routing added to
/// <see cref="TranscriptionEndpointResolver"/>: every legacy mode now resolves to the DevThrottle
/// account endpoint, and pins the DevThrottle default models. Pure - no config, no network.
/// </summary>
public sealed class ProviderRoutingTests
{
    [Fact]
    public void ResolveWingman_DevThrottle_UsesProxyBaseAndGlmModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman(TranscriptionMode.DevThrottle);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("zai-org/GLM-5.2", ep.Model);
    }

    [Fact]
    public void ResolveWingman_LegacyByo_UsesDevThrottleTarget()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("zai-org/GLM-5.2", ep.Model);
    }

    [Fact]
    public void ResolveWingmanFast_DevThrottle_UsesProxyBaseAndFastModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast(TranscriptionMode.DevThrottle);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", ep.Model);
    }

    [Fact]
    public void ResolveWingmanFast_LegacyByo_UsesDevThrottleTarget()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", ep.Model);
    }

    [Fact]
    public void ResolveTts_DevThrottle_UsesProxyBaseAndTtsModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveTts(TranscriptionMode.DevThrottle);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("hexgrad/Kokoro-82M", ep.Model);
    }

    [Fact]
    public void ResolveTts_LegacyByo_UsesDevThrottleTarget()
    {
        var ep = TranscriptionEndpointResolver.ResolveTts(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("hexgrad/Kokoro-82M", ep.Model);
    }
}
