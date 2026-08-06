using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Car Mode runs its OWN model (a fast tier + tool_choice=required), separate from the Wingman. These
/// pin the resolution precedence the Architect settled: the CC_CARMODE_MODEL env override wins, then the
/// user's saved setting (honored only when it is a DevThrottle internal included id - issue #1360), then
/// the included fast wingman default.
/// </summary>
public sealed class CarModeModelConfigTests
{
    [Fact]
    public void Default_IsTheIncludedFastWingmanId()
    {
        Assert.Equal("devthrottle/wingman-fast", CarModeModelConfig.Default);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleWingmanFastModel, CarModeModelConfig.Default);
    }

    [Fact]
    public void Resolve_EnvOverride_Wins()
    {
        var original = Environment.GetEnvironmentVariable(CarModeModelConfig.EnvVar);
        try
        {
            // The env var is a per-install debug switch; an INCLUDED id is honored.
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, "devthrottle/wingman");
            Assert.Equal("devthrottle/wingman", CarModeModelConfig.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, original);
        }
    }

    /// <summary>
    /// The environment-override revert-proof for the Included AI rule (issue #1360, inspection round):
    /// Car Mode's model rides the DevThrottle deployment credential, so a catalog id set through
    /// CC_CARMODE_MODEL would bill credits on an internal feature. Put the honor-any-nonblank-env read
    /// back and this goes red.
    /// </summary>
    [Theory]
    [InlineData("Qwen/Qwen2.5-72B-Instruct")]
    [InlineData("zai-org/GLM-5.2")]
    [InlineData("kimi-k2")]
    public void Resolve_EnvOverrideCatalogId_FallsForwardToTheSavedSettingOrDefault(string catalogId)
    {
        var original = Environment.GetEnvironmentVariable(CarModeModelConfig.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, catalogId);
            // A non-included env value is treated as "not chosen": resolution falls forward to the
            // saved-setting-or-default leg, exactly as a non-included saved value does.
            Assert.Equal(CarModeModelConfig.Get(), CarModeModelConfig.Resolve());
            Assert.NotEqual(catalogId, CarModeModelConfig.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, original);
        }
    }

    [Fact]
    public void Resolve_WithoutEnv_FallsBackToTheSavedSettingOrDefault()
    {
        var original = Environment.GetEnvironmentVariable(CarModeModelConfig.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, null);
            // With no env override the effective model is exactly the persisted user setting (Get()),
            // which is the included fast default when nothing is saved. Never empty.
            var resolved = CarModeModelConfig.Resolve();
            Assert.Equal(CarModeModelConfig.Get(), resolved);
            Assert.False(string.IsNullOrWhiteSpace(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, original);
        }
    }
}

/// <summary>
/// The saved-setting half of the Car Mode resolution, against a private storage root. The catalog-id
/// case is the Car Mode revert-proof for the Included AI rule (issue #1360): put the old
/// honor-any-saved-string read back and it goes red.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class CarModeModelConfigSavedSettingTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public CarModeModelConfigSavedSettingTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-carmodel-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("zai-org/GLM-5.2")]
    [InlineData("Qwen/Qwen2.5-72B-Instruct")]
    [InlineData("kimi-k2")]
    public void Get_SavedCatalogId_FallsForwardToIncludedDefault(string catalogId)
    {
        CarModeModelConfig.Set(catalogId);
        Assert.Equal(CarModeModelConfig.Default, CarModeModelConfig.Get());
    }

    [Fact]
    public void Get_SavedIncludedId_IsHonored()
    {
        CarModeModelConfig.Set("devthrottle/wingman");
        Assert.Equal("devthrottle/wingman", CarModeModelConfig.Get());
    }
}
