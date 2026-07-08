using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="WingmanModelConfig"/> resolves the hosted wingman chat model: a real saved id is honored;
/// an unset or stale Claude-alias value falls forward to the DevThrottle default so the wingman never
/// calls the proxy with a model it cannot run.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class WingmanModelConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public WingmanModelConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-wingmodel-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_Unset_UsesProviderDefault()
    {
        Assert.Equal("zai-org/GLM-5.2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("zai-org/GLM-5.2", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    public void Resolve_StaleClaudeAlias_FallsForwardToProviderDefault(string alias)
    {
        WingmanModelConfig.Set(alias);   // a legacy warm-brain value the hosted proxy cannot run
        Assert.Equal("zai-org/GLM-5.2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
    }

    [Fact]
    public void Resolve_RealHostedModel_IsHonored()
    {
        WingmanModelConfig.Set("kimi-k2");
        Assert.Equal("kimi-k2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("kimi-k2", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Fact]
    public void Set_PersistsToBrainModelKey()
    {
        WingmanModelConfig.Set("zai-org/GLM-5.2");
        Assert.Equal("zai-org/GLM-5.2", CcDirectorConfigService.ReadRaw()["brain_model"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveFast_Unset_UsesProviderFastDefault()
    {
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", WingmanModelConfig.ResolveFast(TranscriptionMode.DevThrottle));
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", WingmanModelConfig.ResolveFast(TranscriptionMode.Byo));
    }

    [Fact]
    public void ResolveFast_StaleClaudeAlias_FallsForwardToProviderFastDefault()
    {
        WingmanModelConfig.SetFast("opus");
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", WingmanModelConfig.ResolveFast(TranscriptionMode.DevThrottle));
    }

    [Fact]
    public void SetFast_PersistsToSeparateKey_AndDoesNotTouchThinking()
    {
        WingmanModelConfig.Set("zai-org/GLM-5.2");
        WingmanModelConfig.SetFast("Qwen/Qwen2.5-72B-Instruct");

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("zai-org/GLM-5.2", raw["brain_model"]!.GetValue<string>());
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", raw["brain_model_fast"]!.GetValue<string>());
    }

    [Fact]
    public void Resolve_ByRole_DispatchesToThinkingOrFast()
    {
        WingmanModelConfig.Set("zai-org/GLM-5.2");
        WingmanModelConfig.SetFast("Qwen/Qwen2.5-72B-Instruct");

        Assert.Equal("zai-org/GLM-5.2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle, WingmanModelRole.Thinking));
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle, WingmanModelRole.Fast));
    }
}
