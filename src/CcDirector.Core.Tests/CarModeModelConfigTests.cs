using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Car Mode runs its OWN model (a fast tier + tool_choice=required), separate from the Wingman. These
/// pin the resolution precedence the Architect settled: the CC_CARMODE_MODEL env override wins, then the
/// user's saved setting, then the Qwen2.5-72B default.
/// </summary>
public sealed class CarModeModelConfigTests
{
    [Fact]
    public void Default_IsTheFastQwenTier()
    {
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", CarModeModelConfig.Default);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleWingmanFastModel, CarModeModelConfig.Default);
    }

    [Fact]
    public void Resolve_EnvOverride_Wins()
    {
        var original = Environment.GetEnvironmentVariable(CarModeModelConfig.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CarModeModelConfig.EnvVar, "zai-org/GLM-5.2");
            Assert.Equal("zai-org/GLM-5.2", CarModeModelConfig.Resolve());
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
            // which is the Qwen default when nothing is saved. Never empty.
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
