using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="TtsVoiceConfig"/> persists the text-to-speech voice. Voices are dynamic and
/// provider-specific (each speech model hands back its own set), so ANY non-empty id is accepted and the
/// default is the DevThrottle hosted voice. Runs against an isolated CC_DIRECTOR_ROOT; the CcStorageRoot collection
/// serializes root-mutating tests.
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
    public void Get_NoConfig_ReturnsEmpty()
        => Assert.Equal("", TtsVoiceConfig.Get());

    [Fact]
    public void Resolve_NoConfig_UsesProviderDefault()
    {
        Assert.Equal("af_bella", TtsVoiceConfig.Resolve(TranscriptionMode.DevThrottle));   // Kokoro default
        Assert.Equal("af_bella", TtsVoiceConfig.Resolve(TranscriptionMode.Byo));           // Legacy BYO migrates forward
    }

    [Theory]
    [InlineData("af_bella")]
    [InlineData("am_onyx")]
    [InlineData("nova")]
    [InlineData("bf_emma")]
    public void SetThenResolve_AnyVoiceIsAcceptedAndHonored(string voice)
    {
        // Any non-empty id - no fixed allow-list (Kokoro voices are not OpenAI voices).
        TtsVoiceConfig.Set(voice);
        Assert.Equal(voice, TtsVoiceConfig.Get());
        Assert.Equal(voice, TtsVoiceConfig.Resolve(TranscriptionMode.DevThrottle));
        Assert.True(File.Exists(CcStorage.ConfigJson()));
    }

    [Fact]
    public void Set_Trims()
    {
        TtsVoiceConfig.Set("  am_adam ");
        Assert.Equal("am_adam", TtsVoiceConfig.Get());
    }

    [Fact]
    public void Set_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => TtsVoiceConfig.Set("   "));

    [Fact]
    public void Set_DoesNotDropSiblingConfigKeys()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject { ["transcription_mode"] = "devthrottle" });
        TtsVoiceConfig.Set("af_nova");

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("af_nova", raw["tts_voice"]!.GetValue<string>());
        Assert.Equal("devthrottle", raw["transcription_mode"]!.GetValue<string>());
    }

    [Fact]
    public void FallbackVoices_IsTheFallbackSet()
    {
        Assert.Contains("nova", TtsVoiceConfig.FallbackVoices);
        Assert.Contains("shimmer", TtsVoiceConfig.FallbackVoices);
    }
}
