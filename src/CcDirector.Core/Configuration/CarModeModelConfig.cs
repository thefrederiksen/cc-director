using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The chat model Car Mode's fleet brain runs on. Car Mode deliberately runs a DIFFERENT model from the
/// Wingman (validated 2026-07-11): a FAST hosted model paired with tool_choice=required + a speak_answer
/// tool, which is fast AND reliably chooses tools - so Car Mode has its OWN first-class model setting,
/// separate from any Wingman model, surfaced on the AI Settings screen.
///
/// Resolution precedence (settled with the Architect):
///   1. the <c>CC_CARMODE_MODEL</c> environment variable (a per-install override / debug switch) - wins,
///      honored only when it is a DevThrottle internal included id (issue #1360);
///   2. the user's saved setting (config.json <c>car_mode_model</c>) - what the AI Settings dropdown
///      writes - honored only when it is a DevThrottle internal included id (issue #1360);
///   3. the default, <see cref="Default"/> (the fast wingman id), the fast tier proven cleanest under
///      the guardrail. The thinking wingman id remains a selectable option in the dropdown (slower but
///      a strong tool-caller).
/// </summary>
public static class CarModeModelConfig
{
    /// <summary>The config.json key the chosen Car Mode model is stored under.</summary>
    public const string ConfigKey = "car_mode_model";

    /// <summary>The environment variable that overrides everything (per-install / debugging).</summary>
    public const string EnvVar = "CC_CARMODE_MODEL";

    /// <summary>The default Car Mode model: the fast tier (Qwen2.5-72B) proven cleanest under
    /// tool_choice=required + speak_answer.</summary>
    public const string Default = TranscriptionEndpointResolver.DevThrottleWingmanFastModel;

    /// <summary>
    /// The user's saved Car Mode model (config.json <c>car_mode_model</c>), or <see cref="Default"/> when
    /// unset/blank. Does NOT consult the environment override - this is the persisted USER setting the AI
    /// Settings screen shows. Use <see cref="Resolve"/> for the effective model the brain runs.
    /// </summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var model = v.GetValue<string>().Trim();
            // Car Mode is an internal included feature (issue #1360): only a DevThrottle internal id
            // is honored. A catalog id saved by an older release would bill credits, so it falls
            // forward to the included default.
            if (TranscriptionEndpointResolver.IsDevThrottleIncludedModel(model))
                return model;
        }
        return Default;
    }

    /// <summary>
    /// The EFFECTIVE Car Mode model the brain runs, applying the full precedence: the
    /// <see cref="EnvVar"/> environment override wins, then the user's saved setting, then
    /// <see cref="Default"/>. Read at call time so a settings change (or an env change on restart) is
    /// honoured on the next turn. The environment override is subject to the SAME included-id rule as
    /// the saved setting (issue #1360): Car Mode is an internal included feature and its model is sent
    /// with the DevThrottle deployment credential, so a catalog id here would bill credits no matter
    /// which knob it arrived through. A non-included environment value falls forward exactly as a
    /// non-included saved value does. Returns the PROVEN type, so what this resolves can be handed
    /// straight to the deployment credential.
    /// </summary>
    public static IncludedModelId Resolve()
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        return IncludedModelId.TryMint(env)
               ?? IncludedModelId.MintOrFallForward(Get(), IncludedModelId.WingmanFast);
    }

    /// <summary>Persist the chosen Car Mode model to config.json (merge-patch).</summary>
    public static void Set(string model)
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = model.Trim() });
    }
}
