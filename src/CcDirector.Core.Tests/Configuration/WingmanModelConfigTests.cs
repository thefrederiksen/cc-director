using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="WingmanModelConfig"/> resolves the hosted wingman chat model. Since the Included AI
/// mission (issue #1360) the wingman runs ONLY on DevThrottle internal included ids
/// (devthrottle/wingman, devthrottle/wingman-fast): a saved internal id is honored; everything else -
/// unset, a legacy Claude alias, or a CATALOG model id (which would bill credits on an internal
/// feature) - falls forward to the included default. The catalog-id tests here are the revert-proof
/// for that rule: put the old honor-any-saved-string resolution back and they go red.
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
    public void Resolve_Unset_UsesIncludedDefault()
    {
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    public void Resolve_StaleClaudeAlias_FallsForwardToIncludedDefault(string alias)
    {
        WingmanModelConfig.Set(alias);   // a legacy warm-brain value the hosted proxy cannot run
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
    }

    /// <summary>
    /// REVERT-PROOF for the Included AI inclusion rule (issue #1360): a saved CATALOG model id must
    /// NOT be honored - a wingman pointed at a catalog id bills credits, which the ruling forbids for
    /// an internal feature. The old defaults saved by earlier releases are exactly such ids.
    /// </summary>
    [Theory]
    [InlineData("kimi-k2")]
    [InlineData("zai-org/GLM-5.2")]
    [InlineData("Qwen/Qwen2.5-72B-Instruct")]
    public void Resolve_SavedCatalogId_FallsForwardToIncludedDefault(string catalogId)
    {
        WingmanModelConfig.Set(catalogId);
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Fact]
    public void Resolve_SavedIncludedId_IsHonored()
    {
        WingmanModelConfig.Set("devthrottle/wingman-fast");
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.Resolve(TranscriptionMode.Byo));
    }

    [Fact]
    public void Set_PersistsToBrainModelKey()
    {
        WingmanModelConfig.Set("devthrottle/wingman");
        Assert.Equal("devthrottle/wingman", CcDirectorConfigService.ReadRaw()["brain_model"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveFast_Unset_UsesIncludedFastDefault()
    {
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.ResolveFast(TranscriptionMode.DevThrottle));
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.ResolveFast(TranscriptionMode.Byo));
    }

    [Fact]
    public void ResolveFast_StaleClaudeAlias_FallsForwardToIncludedFastDefault()
    {
        WingmanModelConfig.SetFast("opus");
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.ResolveFast(TranscriptionMode.DevThrottle));
    }

    /// <summary>Fast-role half of the same revert-proof: a saved catalog id on brain_model_fast is not
    /// honored either.</summary>
    [Fact]
    public void ResolveFast_SavedCatalogId_FallsForwardToIncludedFastDefault()
    {
        WingmanModelConfig.SetFast("Qwen/Qwen2.5-72B-Instruct");
        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.ResolveFast(TranscriptionMode.DevThrottle));
    }

    [Fact]
    public void SetFast_PersistsToSeparateKey_AndDoesNotTouchThinking()
    {
        WingmanModelConfig.Set("devthrottle/wingman");
        WingmanModelConfig.SetFast("devthrottle/wingman-fast");

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("devthrottle/wingman", raw["brain_model"]!.GetValue<string>());
        Assert.Equal("devthrottle/wingman-fast", raw["brain_model_fast"]!.GetValue<string>());
    }

    [Fact]
    public void Resolve_ByRole_DispatchesToThinkingOrFast()
    {
        // Cross-saved on purpose so the assert can tell the roles apart: each role must read its OWN key.
        WingmanModelConfig.Set("devthrottle/wingman-fast");
        WingmanModelConfig.SetFast("devthrottle/wingman");

        Assert.Equal("devthrottle/wingman-fast", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle, WingmanModelRole.Thinking));
        Assert.Equal("devthrottle/wingman", WingmanModelConfig.Resolve(TranscriptionMode.DevThrottle, WingmanModelRole.Fast));
    }
}
