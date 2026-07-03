using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The text-to-speech voice for spoken wingman output, persisted in config.json as the top-level
/// string "tts_voice", mirroring the existing "transcription_mode" / "addressing_mode" settings.
/// The names are the OpenAI-compatible voice set, served the same way by both AI providers (OpenAI
/// directly, and the DevThrottle proxy which is OpenAI-compatible), so the one stored value applies
/// whichever provider is selected.
///
/// Read locally by the Gateway text-to-speech path at synthesis time, so a change is honored on the
/// next spoken summary without a restart.
///
/// No-fallback rule: a key that is present but not one of the allowed voices THROWS with the allowed
/// set named, rather than silently picking a voice (see <see cref="Parse"/>).
/// </summary>
public static class TtsVoiceConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "tts_voice";

    /// <summary>The default voice when nothing is configured: the natural "nova" voice the phone uses.</summary>
    public const string Default = "nova";

    /// <summary>The selectable OpenAI-compatible voices offered in the settings picker.</summary>
    public static readonly IReadOnlyList<string> AllowedVoices = new[]
    {
        "nova", "alloy", "echo", "fable", "onyx", "shimmer",
    };

    /// <summary>True when <paramref name="value"/> is one of the allowed voices (case-insensitive).</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return AllowedVoices.Contains(value.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Parse/normalize a voice to its lowercase canonical form. Null/empty yields <see cref="Default"/>;
    /// any other unrecognized value THROWS with the allowed set named (no-fallback rule: a typo must
    /// not silently pick a voice).
    /// </summary>
    public static string Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        var v = value.Trim().ToLowerInvariant();
        if (!AllowedVoices.Contains(v))
            throw new ArgumentException(
                $"tts_voice '{value}' is not valid - it must be one of: {string.Join(", ", AllowedVoices)}.", nameof(value));
        return v;
    }

    /// <summary>Resolve the voice: config.json "tts_voice" when set, else <see cref="Default"/>.</summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return Parse(v.GetValue<string>());

        throw new InvalidOperationException(
            "config.json key 'tts_voice' must be a string (one of nova/alloy/echo/fable/onyx/shimmer). " +
            "Fix the value or remove the key to use the default (nova).");
    }

    /// <summary>Persist the voice to config.json (merge-patch, leaving other keys untouched).</summary>
    public static void Set(string voice)
    {
        CcDirectorConfigService.MergePatch(
            new JsonObject { [ConfigKey] = Parse(voice) });
    }
}
