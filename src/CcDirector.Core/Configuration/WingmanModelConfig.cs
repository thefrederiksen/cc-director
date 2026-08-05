using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The chat model the hosted wingman runs on through DevThrottle. The wingman is a stateless
/// provider-compatible chat call, so the model must be one DevThrottle serves. The choice is stored in
/// the existing config.json "brain_model" key.
///
/// Resolution rule (issue #1360, Included AI): the wingman is an INCLUDED service - it runs on
/// DevThrottle's internal model ids (<c>devthrottle/wingman</c>, <c>devthrottle/wingman-fast</c>),
/// which the hosted proxy meters and never bills to credits. A saved model that is NOT one of the
/// internal ids - a catalog id picked in an older release, or the legacy Claude tier aliases
/// ("opus"/"sonnet"/"haiku") from the old warm claude.exe brain - would bill credits (or not run at
/// all), so it is treated as "not chosen" and resolution falls forward to the included default.
/// Only a DevThrottle internal id is honored as saved.
/// </summary>
public static class WingmanModelConfig
{
    /// <summary>The config.json key the thinking model is stored under (shared with the legacy brain model).</summary>
    public const string ConfigKey = "brain_model";

    /// <summary>The config.json key the fast wingman model is stored under.</summary>
    public const string FastConfigKey = "brain_model_fast";

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
            // Only a DevThrottle internal included id is honored (issue #1360): anything else - a
            // catalog id, a legacy Claude alias, an old default - would bill credits or fail on the
            // proxy, so it falls forward to the included default.
            if (TranscriptionEndpointResolver.IsDevThrottleIncludedModel(model))
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
