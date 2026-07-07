using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The text-to-speech MODEL used for spoken wingman output, persisted in config.json as the top-level
/// string "tts_model". The model list is DYNAMIC - the DevThrottle proxy hands it back from its
/// <c>GET /models?type=speech</c> catalog (hexgrad/Kokoro-82M, Chatterbox) - so this stores whatever id
/// the user picked from that live list, and the DEFAULT is <c>hexgrad/Kokoro-82M</c>
/// (<see cref="Resolve"/>). Read at synthesis time, so a change applies on the next spoken summary.
/// </summary>
public static class TtsModelConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "tts_model";

    /// <summary>The raw saved model id, or empty string when unset. Use <see cref="Resolve"/> for the
    /// provider-aware effective value.</summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is null)
            return "";
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return v.GetValue<string>().Trim();
        throw new InvalidOperationException(
            "config.json key 'tts_model' must be a string (a speech model id). Fix the value or remove the key.");
    }

    /// <summary>The effective speech model: the saved value when set, else the default (Kokoro).</summary>
    public static string Resolve()
    {
        var saved = Get();
        return saved.Length > 0 ? saved : TranscriptionEndpointResolver.DefaultTtsModel();
    }

    /// <summary>Persist the model (any non-empty id; the catalog is dynamic, so there is no allow-list).</summary>
    public static void Set(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("model must be a non-empty id", nameof(model));
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = model.Trim() });
    }
}
