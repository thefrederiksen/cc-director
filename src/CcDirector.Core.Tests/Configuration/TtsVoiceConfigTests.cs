using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="TtsVoiceConfig"/> persists the text-to-speech voice to config.json so it survives a
/// restart, and applies the no-fallback rule (a present-but-unknown voice throws). Each method runs
/// against an isolated CC_DIRECTOR_ROOT; the CcStorageRoot collection serializes classes that mutate
/// the process-wide root.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class TtsVoiceConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TtsVoiceConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ttsvoice-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_DefaultsToNova()
        => Assert.Equal("nova", TtsVoiceConfig.Get());

    [Fact]
    public void Default_IsNova()
        => Assert.Equal("nova", TtsVoiceConfig.Default);

    [Theory]
    [InlineData("nova")]
    [InlineData("alloy")]
    [InlineData("echo")]
    [InlineData("fable")]
    [InlineData("onyx")]
    [InlineData("shimmer")]
    public void SetThenGet_EachAllowedVoice_PersistsAcrossReread(string voice)
    {
        TtsVoiceConfig.Set(voice);
        // A fresh Get() re-reads config.json from disk - the same path a restarted process takes.
        Assert.Equal(voice, TtsVoiceConfig.Get());
        Assert.True(File.Exists(CcStorage.ConfigJson()));
    }

    [Fact]
    public void Set_NormalizesCasingAndWhitespace()
    {
        TtsVoiceConfig.Set("  NOVA ");
        Assert.Equal("nova", TtsVoiceConfig.Get());
    }

    [Fact]
    public void Set_UnknownVoice_Throws()   // no-fallback rule: a typo must not silently pick a voice
        => Assert.Throws<ArgumentException>(() => TtsVoiceConfig.Set("robot"));

    [Fact]
    public void Set_DoesNotDropSiblingConfigKeys()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject { ["transcription_mode"] = "devthrottle" });
        TtsVoiceConfig.Set("onyx");

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("onyx", raw["tts_voice"]!.GetValue<string>());
        Assert.Equal("devthrottle", raw["transcription_mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("nova", true)]
    [InlineData("SHIMMER", true)]
    [InlineData("robot", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_MatchesAllowedSet(string? value, bool expected)
        => Assert.Equal(expected, TtsVoiceConfig.IsValid(value));

    [Fact]
    public void Parse_NullOrEmpty_ReturnsDefault()
    {
        Assert.Equal("nova", TtsVoiceConfig.Parse(null));
        Assert.Equal("nova", TtsVoiceConfig.Parse("   "));
    }
}
