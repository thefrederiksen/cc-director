using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The chat model the hosted wingman runs on through DevThrottle. The wingman is a stateless
/// provider-compatible chat call, so the model must be one DevThrottle serves. The user can pick any
/// model from the live catalog (the AI tab's "Wingman model" dropdown); the choice is stored in the
/// existing config.json "brain_model" key.
///
/// Resolution rule (why this exists rather than reading brain_model directly): brain_model historically
/// defaulted to a Claude tier alias ("opus"/"sonnet"/"haiku") for the old warm claude.exe brain, which
/// the hosted proxy does NOT serve. So a stale or unset brain_model must fall forward to the provider's
/// default hosted model (glm-5.2 / gpt-5.5), never a Claude alias - otherwise the wingman would call the
/// proxy with a model it cannot run. A real hosted model id the user saved is honored as-is.
/// </summary>
public static class WingmanModelConfig
{
    /// <summary>The config.json key the thinking model is stored under (shared with the legacy brain model).</summary>
    public const string ConfigKey = "brain_model";

    /// <summary>The config.json key the fast wingman model is stored under.</summary>
    public const string FastConfigKey = "brain_model_fast";

    /// <summary>Claude tier aliases that predate the hosted wingman and cannot run on the proxy; treated
    /// as "not chosen" so they fall forward to the provider default.</summary>
    private static readonly HashSet<string> ClaudeAliases =
        new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet", "haiku" };

    /// <summary>
    /// The hosted wingman model for <paramref name="mode"/>: the saved brain_model when it is a real
    /// hosted model id, else the provider default (<see cref="TranscriptionEndpointResolver.ResolveWingman"/>).
    /// </summary>
    public static string Resolve(TranscriptionMode mode) =>
        ResolveKey(ConfigKey, () => TranscriptionEndpointResolver.ResolveWingman(mode).Model);

    /// <summary>
    /// The hosted fast wingman model for <paramref name="mode"/>: the saved brain_model_fast when it
    /// is a real hosted model id, else the provider default
    /// (<see cref="TranscriptionEndpointResolver.ResolveWingmanFast"/>).
    /// </summary>
    public static string ResolveFast(TranscriptionMode mode) =>
        ResolveKey(FastConfigKey, () => TranscriptionEndpointResolver.ResolveWingmanFast(mode).Model);

    /// <summary>Resolve the model for the requested wingman role.</summary>
    public static string Resolve(TranscriptionMode mode, WingmanModelRole role) => role switch
    {
        WingmanModelRole.Thinking => Resolve(mode),
        WingmanModelRole.Fast => ResolveFast(mode),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown wingman model role"),
    };

    /// <summary>Persist the chosen wingman model to config.json (merge-patch).</summary>
    public static void Set(string model)
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = model.Trim() });
    }

    /// <summary>Persist the chosen fast wingman model to config.json (merge-patch).</summary>
    public static void SetFast(string model)
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [FastConfigKey] = model.Trim() });
    }

    private static string ResolveKey(string configKey, Func<string> providerDefault)
    {
        var node = CcDirectorConfigService.ReadRaw()[configKey];
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var model = v.GetValue<string>().Trim();
            if (model.Length > 0 && !ClaudeAliases.Contains(model))
                return model;
        }
        return providerDefault();
    }
}

/// <summary>Which hosted model tier a wingman call should use.</summary>
public enum WingmanModelRole
{
    /// <summary>Quality-sensitive paths such as direct Wingman conversation and product Q&amp;A.</summary>
    Thinking = 0,

    /// <summary>Latency-sensitive response-only paths such as summaries and menu handling.</summary>
    Fast = 1,
}
