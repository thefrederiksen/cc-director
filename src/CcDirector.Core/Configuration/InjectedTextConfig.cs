using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether the user has chosen to run their own injected text instead of the one DevThrottle ships.
///
/// Persisted in config.json under the top-level object "injected_text":
///   - "use_yours" (bool) - run the user's own text (base/injected-text/yours.txt) instead of ours.
///                          DEFAULT FALSE: a fresh install runs our text, which is what it did
///                          before this setting existed.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than silently
/// picking a default. For THIS setting that matters more than most - silently reading a malformed
/// "use_yours" as false would inject our text, including our policy text, into the sessions of
/// someone who had explicitly turned it off. That is the exact failure this feature exists to
/// prevent, so it fails loudly instead.
/// </summary>
public sealed record InjectedTextConfig(bool UseYours)
{
    /// <summary>The default posture: DevThrottle's text, as it was before the user could choose.</summary>
    public static readonly InjectedTextConfig Default = new(UseYours: false);

    /// <summary>Read the effective setting from config.json's "injected_text" object.</summary>
    public static InjectedTextConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["injected_text"];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                "config.json key 'injected_text' must be an object. " +
                "Fix the value or remove the key to use the DevThrottle text.");

        return new InjectedTextConfig(UseYours: ReadBool(obj, "use_yours", Default.UseYours));
    }

    /// <summary>Record the user's choice of whose text is live.</summary>
    public static void SetUseYours(bool useYours)
        => CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["injected_text"] = new JsonObject { ["use_yours"] = useYours },
        });

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key 'injected_text.{key}' must be true or false. " +
            "Fix the value or remove the key to use the DevThrottle text.");
    }
}
