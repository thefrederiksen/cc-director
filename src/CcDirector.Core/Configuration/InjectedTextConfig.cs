using System.Text.Json.Nodes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The whole of the injected-text choice: whether the user runs their own text instead of the one
/// DevThrottle ships, and - when they do - the text itself.
/// </summary>
/// <param name="UseYours">Run the user's own text (<see cref="Yours"/>) instead of the shipped default.</param>
/// <param name="Yours">The user's own text, kept even while ours is live so they can switch back to it
/// without rewriting it. Null when they have never written one.</param>
public sealed record InjectedTextSettings(bool UseYours, string? Yours)
{
    /// <summary>The default posture: DevThrottle's text, and no custom text written yet.</summary>
    public static readonly InjectedTextSettings Default = new(UseYours: false, Yours: null);
}

/// <summary>
/// The GATEWAY-OWNED, per-user injected-text setting. It is fleet-wide by construction: every one of
/// the user's Directors talks to their one Gateway, so this single Gateway value IS "the same injected
/// text on all my machines". The Cockpit edits it here; each Director downloads it and caches it (see
/// <see cref="Sessions.InjectedTextStore"/>) so a session launch never waits on - or fails without -
/// the network.
///
/// Persisted in the Gateway's <c>config.json</c> under the top-level object "injected_text":
///   - "use_yours" (bool)   - run the user's own text instead of ours. DEFAULT FALSE.
///   - "yours"      (string) - the user's own text. Absent until they write one.
/// This is the same store the other Gateway settings use (<see cref="SnoozeDefaultConfig"/>,
/// <see cref="TelemetryConsentConfig"/>).
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than silently
/// picking a default. For THIS setting that matters more than most - silently reading a malformed
/// "use_yours" as false would inject our text, including our policy text, into the sessions of someone
/// who had explicitly turned it off. That is the exact failure this feature exists to prevent, so it
/// fails loudly instead.
/// </summary>
public static class InjectedTextConfig
{
    /// <summary>The config.json top-level key holding the injected-text object.</summary>
    public const string Key = "injected_text";

    /// <summary>The text DevThrottle ships, straight from the application - never from disk. The Cockpit
    /// shows it so a user on their own version can always read the current default and adopt it.</summary>
    public static string Ours => FleetPreambleTemplate.Default;

    /// <summary>Read the effective setting from the Gateway's config.json "injected_text" object.</summary>
    /// <exception cref="InvalidOperationException">The key is present but malformed.</exception>
    public static InjectedTextSettings Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[Key];
        if (node is null)
        {
            FileLog.Write("[InjectedTextConfig] Get: no persisted value -> DevThrottle text, no custom text");
            return InjectedTextSettings.Default;
        }

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"config.json key '{Key}' must be an object. " +
                "Fix the value or remove the key to use the DevThrottle text.");

        var useYours = ReadBool(obj, "use_yours", InjectedTextSettings.Default.UseYours);
        var yours = ReadString(obj, "yours");
        FileLog.Write($"[InjectedTextConfig] Get: use_yours={useYours}, has_yours={yours is not null}");
        return new InjectedTextSettings(useYours, yours);
    }

    /// <summary>
    /// True when <paramref name="settings"/> is a state this setting will accept, else a plain-English
    /// description of the problem to show the user. Pure, so the endpoint can validate a request body
    /// before writing anything. The rules: the user's text, if present, must be a renderable template;
    /// and you cannot run your own text without having written some.
    /// </summary>
    public static string? Validate(InjectedTextSettings settings)
    {
        if (settings.Yours is not null)
        {
            var problem = FleetPreambleRenderer.Validate(settings.Yours);
            if (problem is not null)
                return problem;
        }

        // Running your own text needs a text to run - but it MAY be empty, which is the user's right to
        // inject nothing at all (fleet commands and policy included). Empty is a value; absent is not.
        if (settings.UseYours && settings.Yours is null)
            return "To run your own injected text, provide it. It may be empty - that injects nothing - " +
                   "but it cannot be absent.";

        return null;
    }

    /// <summary>
    /// Persist the injected-text setting to the Gateway's config.json, merging into the existing file so
    /// no other section is dropped. Both fields are written together, so the object is always fully
    /// specified rather than half-merged.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="settings"/> fails <see cref="Validate"/>.</exception>
    public static void Set(InjectedTextSettings settings)
    {
        var problem = Validate(settings);
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));

        FileLog.Write($"[InjectedTextConfig] Set: use_yours={settings.UseYours}, has_yours={settings.Yours is not null}");
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            [Key] = new JsonObject
            {
                ["use_yours"] = settings.UseYours,
                ["yours"] = settings.Yours is null ? null : JsonValue.Create(settings.Yours),
            },
        });
        FileLog.Write("[InjectedTextConfig] Set: persisted");
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == System.Text.Json.JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key '{Key}.{key}' must be true or false. " +
            "Fix the value or remove the key to use the DevThrottle text.");
    }

    private static string? ReadString(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null)
            return null;
        if (node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
            return v.GetValue<string>();

        throw new InvalidOperationException(
            $"config.json key '{Key}.{key}' must be a string. " +
            "Fix the value or remove the key to use the DevThrottle text.");
    }
}
