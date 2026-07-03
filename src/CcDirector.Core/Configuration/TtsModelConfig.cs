using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The text-to-speech MODEL used for spoken wingman output, persisted in config.json as the top-level
/// string "tts_model", mirroring "tts_voice" / "transcription_mode". Unlike the voice (a fixed set), the
/// model list is DYNAMIC - the AI provider hands it back from its <c>GET /models?type=speech</c> catalog
/// (e.g. <c>tts-1</c>, <c>tts-1-hd</c>, <c>kokoro</c>) - so this stores whatever id the user picked from
/// that live list. Read at synthesis time, so a change applies on the next spoken summary.
///
/// The default (<c>tts-1</c>) is served by both providers (OpenAI directly, and the DevThrottle proxy
/// which is OpenAI-compatible), so a fresh install speaks without any setup. An empty value is treated
/// as "use the default" rather than an error (there is no fixed allowed set to validate against).
/// </summary>
public static class TtsModelConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "tts_model";

    /// <summary>The default speech model when nothing is configured. Served by both providers.</summary>
    public const string Default = "tts-1";

    /// <summary>Normalize a model id: trimmed; null/empty yields <see cref="Default"/>.</summary>
    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? Default : value.Trim();

    /// <summary>Resolve the model: config.json "tts_model" when set, else <see cref="Default"/>.</summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return Normalize(v.GetValue<string>());

        throw new InvalidOperationException(
            "config.json key 'tts_model' must be a string (a speech model id, e.g. \"tts-1\"). " +
            "Fix the value or remove the key to use the default (tts-1).");
    }

    /// <summary>Persist the model to config.json (merge-patch, leaving other keys untouched).</summary>
    public static void Set(string model)
    {
        CcDirectorConfigService.MergePatch(
            new JsonObject { [ConfigKey] = Normalize(model) });
    }
}
