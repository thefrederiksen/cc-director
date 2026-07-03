using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="WingmanModelConfig"/> resolves the hosted wingman chat model per provider: a real saved id
/// is honored; an unset or stale Claude-alias value falls forward to the provider default (glm-5.2 /
/// gpt-5.5) so the wingman never calls the proxy with a model it cannot run.
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
        Assert.Equal("glm-5.2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("gpt-5.5", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    public void Resolve_StaleClaudeAlias_FallsForwardToProviderDefault(string alias)
    {
        WingmanModelConfig.Set(alias);   // a legacy warm-brain value the hosted proxy cannot run
        Assert.Equal("glm-5.2", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
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
        WingmanModelConfig.Set("glm-5.2");
        Assert.Equal("glm-5.2", CcDirectorConfigService.ReadRaw()["brain_model"]!.GetValue<string>());
    }
}
