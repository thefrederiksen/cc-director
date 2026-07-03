using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The text-to-speech voice for spoken wingman output, persisted in config.json as the top-level string
/// "tts_voice". Voices are DYNAMIC and provider-specific - each speech model hands back its own voice
/// list from the provider's <c>/models</c> catalog (OpenAI has nova/alloy/...; the DevThrottle proxy's
/// Kokoro has af_bella/am_onyx/...), and the two sets do NOT overlap. So any non-empty id is accepted
/// here (never a fixed allow-list), and the DEFAULT is provider-aware (<see cref="Resolve"/>).
///
/// Read at synthesis time, so a change applies on the next spoken summary.
/// </summary>
public static class TtsVoiceConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "tts_voice";

    /// <summary>The OpenAI voice set - used only to populate the voice dropdown for the OpenAI provider
    /// (whose flat /models list carries no voices). The DevThrottle voices come from its live catalog.</summary>
    public static readonly IReadOnlyList<string> OpenAiVoices = new[]
    {
        "nova", "alloy", "echo", "fable", "onyx", "shimmer",
    };

    /// <summary>The raw saved voice, or empty string when unset. Use <see cref="Resolve"/> for the
    /// provider-aware effective value.</summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is null)
            return "";
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return v.GetValue<string>().Trim();
        throw new InvalidOperationException(
            "config.json key 'tts_voice' must be a string (a voice id). Fix the value or remove the key.");
    }

    /// <summary>The effective voice for <paramref name="mode"/>: the saved value when set, else the
    /// provider default (Kokoro's af_bella for DevThrottle, nova for OpenAI).</summary>
    public static string Resolve(TranscriptionMode mode)
    {
        var saved = Get();
        return saved.Length > 0 ? saved : TranscriptionEndpointResolver.DefaultTtsVoice(mode);
    }

    /// <summary>Persist the voice (any non-empty id; the catalog is dynamic, so there is no allow-list).</summary>
    public static void Set(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("voice must be a non-empty id", nameof(voice));
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = voice.Trim() });
    }
}
