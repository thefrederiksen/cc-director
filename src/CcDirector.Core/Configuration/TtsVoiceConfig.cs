using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The text-to-speech voice for spoken wingman output, persisted in config.json as the top-level string
/// "tts_voice". Voices are DYNAMIC - each speech model hands back its own voice list from the DevThrottle
/// proxy's <c>/models</c> catalog (Kokoro has af_bella/am_onyx/...). So any non-empty id is accepted here
/// (never a fixed allow-list), and the DEFAULT is Kokoro's <c>af_bella</c> (<see cref="Resolve"/>).
///
/// Read at synthesis time, so a change applies on the next spoken summary.
/// </summary>
public static class TtsVoiceConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "tts_voice";

    /// <summary>The raw saved voice, or empty string when unset. Use <see cref="Resolve"/> for the
    /// effective value.</summary>
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

    /// <summary>The effective voice: the saved value when set, else the default (Kokoro's af_bella).</summary>
    public static string Resolve()
    {
        var saved = Get();
        return saved.Length > 0 ? saved : TranscriptionEndpointResolver.DefaultTtsVoice();
    }

    /// <summary>Persist the voice (any non-empty id; the catalog is dynamic, so there is no allow-list).</summary>
    public static void Set(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("voice must be a non-empty id", nameof(voice));
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = voice.Trim() });
    }
}
