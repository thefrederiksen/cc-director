using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether Car Mode keeps its hosted model and text-to-speech provider WARM while the owner is using it
/// (Car Mode performance round). The measured evidence is conclusive that cold-start is the dominant felt
/// latency (the model ran ~9s cold vs ~1.5s warm, text-to-speech ~4.7s cold vs ~1.3s warm - roughly eleven
/// seconds of cold-start swing). So Car Mode fires a tiny warmup the instant the owner taps Start and a
/// small keep-warm ping every few minutes WHILE Car Mode is open, so the providers are hot before the
/// first utterance and stay hot for the drive. Credits are spent ONLY during active use, never 24/7.
///
/// DEFAULT ON for Car Mode: the owner prioritizes speed and credits are not a constraint here. The flag is
/// the belt - a per-install env override or a saved setting can turn it off.
///
/// Resolution precedence (mirrors <see cref="CarModeModelConfig"/>):
///   1. the <c>CC_CARMODE_KEEPWARM</c> environment variable ("0"/"false"/"off" disables) - wins;
///   2. the user's saved setting (config.json <c>car_mode_keep_warm</c>);
///   3. the default, ON.
/// </summary>
public static class CarModeKeepWarmConfig
{
    /// <summary>The config.json key the keep-warm choice is stored under.</summary>
    public const string ConfigKey = "car_mode_keep_warm";

    /// <summary>The environment variable that overrides everything (per-install / debugging).</summary>
    public const string EnvVar = "CC_CARMODE_KEEPWARM";

    /// <summary>Keep-warm is ON by default for Car Mode.</summary>
    public const bool Default = true;

    /// <summary>
    /// The EFFECTIVE keep-warm setting, applying the full precedence: the <see cref="EnvVar"/> override
    /// wins, then the saved setting, then <see cref="Default"/> (ON). Read at call time so a change is
    /// honoured on the next warmup without a restart.
    /// </summary>
    public static bool Enabled()
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var e = env.Trim().ToLowerInvariant();
            if (e is "0" or "false" or "off" or "no") return false;
            if (e is "1" or "true" or "on" or "yes") return true;
        }
        return Get();
    }

    /// <summary>The user's saved keep-warm setting (config.json <c>car_mode_keep_warm</c>), or
    ///  <see cref="Default"/> when unset. Does NOT consult the environment override.</summary>
    public static bool Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is JsonValue v)
        {
            var kind = v.GetValueKind();
            if (kind is JsonValueKind.True or JsonValueKind.False) return v.GetValue<bool>();
        }
        return Default;
    }

    /// <summary>Persist the chosen keep-warm setting to config.json (merge-patch).</summary>
    public static void Set(bool enabled)
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = enabled });
    }
}
