using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The wingman (chat-completions) and text-to-speech routing added to
/// <see cref="TranscriptionEndpointResolver"/>: both reuse the transcription base URL + vault key per
/// provider, and pin the provider-correct model (glm-5.2 / gpt-5.5 for the wingman, tts-1 for speech).
/// Pure - no config, no network.
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
    public void ResolveWingman_Byo_UsesOpenAiBaseAndGpt55()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiKeyName, ep.KeyName);
        Assert.Equal("gpt-5.5", ep.Model);
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
    public void ResolveWingmanFast_Byo_UsesOpenAiBaseAndMiniModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiKeyName, ep.KeyName);
        Assert.Equal("gpt-5.5-mini", ep.Model);
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
    public void ResolveTts_Byo_UsesOpenAiBaseAndTtsModel()
    {
        var ep = TranscriptionEndpointResolver.ResolveTts(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiKeyName, ep.KeyName);
        Assert.Equal("tts-1", ep.Model);
    }
}
