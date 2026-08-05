using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The wingman (chat-completions) and text-to-speech routing added to
/// <see cref="TranscriptionEndpointResolver"/>: every legacy mode now resolves to the DevThrottle
/// account endpoint, and pins the DevThrottle default models. Pure - no config, no network.
/// </summary>
public sealed class ProviderRoutingTests
{
    // The wingman, fast wingman, and dictation-cleanup ids are the DEVTHROTTLE INTERNAL included ids
    // (issue #1360, Included AI): the hosted proxy meters them as included services and never bills
    // credits. These pins are the revert-proof for the alias switch - point a constant back at a
    // catalog id (the pre-mission "zai-org/GLM-5.2" / "Qwen/Qwen2.5-72B-Instruct" / "o4-mini") and
    // they go red, because a catalog id bills credits on an internal feature.

    [Fact]
    public void ResolveWingman_DevThrottle_UsesProxyBaseAndIncludedWingmanId()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman(TranscriptionMode.DevThrottle);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("devthrottle/wingman", ep.Model);
    }

    [Fact]
    public void ResolveWingman_LegacyByo_UsesDevThrottleTarget()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingman(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("devthrottle/wingman", ep.Model);
    }

    [Fact]
    public void ResolveWingmanFast_DevThrottle_UsesProxyBaseAndIncludedFastId()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast(TranscriptionMode.DevThrottle);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("devthrottle/wingman-fast", ep.Model);
    }

    [Fact]
    public void ResolveWingmanFast_LegacyByo_UsesDevThrottleTarget()
    {
        var ep = TranscriptionEndpointResolver.ResolveWingmanFast(TranscriptionMode.Byo);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, ep.KeyName);
        Assert.Equal("devthrottle/wingman-fast", ep.Model);
    }

    [Fact]
    public void DictationCleanup_DefaultsToIncludedCleanupId()
    {
        Assert.Equal("devthrottle/dictation-cleanup", TranscriptionEndpointResolver.DevThrottleDictationCleanupModel);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleDictationCleanupModel, CleanupOrchestrator.DefaultModel);
    }

    [Theory]
    [InlineData("devthrottle/wingman", true)]
    [InlineData("devthrottle/wingman-fast", true)]
    [InlineData("devthrottle/dictation-cleanup", true)]
    [InlineData("zai-org/GLM-5.2", false)]
    [InlineData("Qwen/Qwen2.5-72B-Instruct", false)]
    [InlineData("o4-mini", false)]
    [InlineData("kimi-k2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDevThrottleIncludedModel_AcceptsOnlyTheInternalPrefix(string? model, bool expected)
    {
        Assert.Equal(expected, TranscriptionEndpointResolver.IsDevThrottleIncludedModel(model));
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
