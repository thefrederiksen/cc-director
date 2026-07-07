using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="TtsModelConfig"/> persists the text-to-speech model to config.json. Unlike the voice, the
/// model list is dynamic (the provider's /models catalog), so any non-empty id is accepted; empty is the
/// default. Runs against an isolated CC_DIRECTOR_ROOT; the CcStorageRoot collection serializes root-mutating tests.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class TtsModelConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TtsModelConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ttsmodel-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_ReturnsEmpty()
        => Assert.Equal("", TtsModelConfig.Get());

    [Fact]
    public void Resolve_NoConfig_UsesProviderDefault()
    {
        Assert.Equal("hexgrad/Kokoro-82M", TtsModelConfig.Resolve());
    }

    [Fact]
    public void SetThenResolve_PersistsAndHonored()
    {
        TtsModelConfig.Set("hexgrad/Kokoro-82M");
        Assert.Equal("hexgrad/Kokoro-82M", TtsModelConfig.Get());
        Assert.Equal("hexgrad/Kokoro-82M", TtsModelConfig.Resolve());
        Assert.True(File.Exists(CcStorage.ConfigJson()));
    }

    [Fact]
    public void Set_Trims_And_EmptyThrows()
    {
        TtsModelConfig.Set("  tts-1-hd  ");
        Assert.Equal("tts-1-hd", TtsModelConfig.Get());
        Assert.Throws<ArgumentException>(() => TtsModelConfig.Set("   "));
    }

    [Fact]
    public void Set_DoesNotDropSiblingConfigKeys()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject { ["tts_voice"] = "onyx" });
        TtsModelConfig.Set("kokoro");

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("kokoro", raw["tts_model"]!.GetValue<string>());
        Assert.Equal("onyx", raw["tts_voice"]!.GetValue<string>());
    }
}
