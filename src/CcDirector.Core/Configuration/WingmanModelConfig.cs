using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The chat model the hosted wingman runs on for the SELECTED AI provider. The wingman is a stateless
/// OpenAI-compatible chat call to the provider (glm-5.2 on the DevThrottle proxy, gpt-5.5 on OpenAI), so
/// the model must be one the provider serves. The user can pick any model from the provider's live
/// catalog (the AI tab's "Wingman model" dropdown); the choice is stored in the existing config.json
/// "brain_model" key.
///
/// Resolution rule (why this exists rather than reading brain_model directly): brain_model historically
/// defaulted to a Claude tier alias ("opus"/"sonnet"/"haiku") for the old warm claude.exe brain, which
/// the hosted proxy does NOT serve. So a stale or unset brain_model must fall forward to the provider's
/// default hosted model (glm-5.2 / gpt-5.5), never a Claude alias - otherwise the wingman would call the
/// proxy with a model it cannot run. A real hosted model id the user (or the provider switch) saved is
/// honored as-is.
/// </summary>
public static class WingmanModelConfig
{
    /// <summary>The config.json key the choice is stored under (shared with the legacy brain model).</summary>
    public const string ConfigKey = "brain_model";

    /// <summary>Claude tier aliases that predate the hosted wingman and cannot run on the proxy; treated
    /// as "not chosen" so they fall forward to the provider default.</summary>
    private static readonly HashSet<string> ClaudeAliases =
        new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet", "haiku" };

    /// <summary>
    /// The hosted wingman model for <paramref name="mode"/>: the saved brain_model when it is a real
    /// hosted model id, else the provider default (<see cref="TranscriptionEndpointResolver.ResolveWingman"/>).
    /// </summary>
    public static string Resolve(TranscriptionMode mode)
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var model = v.GetValue<string>().Trim();
            if (model.Length > 0 && !ClaudeAliases.Contains(model))
                return model;
        }
        return TranscriptionEndpointResolver.ResolveWingman(mode).Model;
    }

    /// <summary>Persist the chosen wingman model to config.json (merge-patch).</summary>
    public static void Set(string model)
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = model.Trim() });
    }
}
