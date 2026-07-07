using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The wingman (chat-completions) and text-to-speech routing on
/// <see cref="TranscriptionEndpointResolver"/>: both reuse the DevThrottle transcription base URL + vault
/// key, and pin the DevThrottle-proxy-correct model (glm-5.2 for the wingman, Kokoro for speech). Pure -
/// no config, no network.
/// </summary>
public sealed class ProviderRoutingTests
{
    [Fact]
    public void ResolveWingman_UsesProxyBaseAndGlmModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman();
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("zai-org/GLM-5.2", ep.Model);
    }

    [Fact]
    public void ResolveWingmanFast_UsesProxyBaseAndFastModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast();
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", ep.Model);
    }

    [Fact]
    public void ResolveTts_UsesProxyBaseAndTtsModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveTts();
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("hexgrad/Kokoro-82M", ep.Model);
    }
}
