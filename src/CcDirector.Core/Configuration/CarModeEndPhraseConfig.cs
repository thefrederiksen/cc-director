using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The spoken sign-off phrase that ends the owner's turn hands-free in Car Mode (default "over and out").
/// Stored on the Gateway, NOT per-device, so the owner can set it once in the Cockpit "Car Mode" settings
/// tab and every surface (his phone especially) picks it up - a device-local value would never reach the
/// phone where Car Mode actually runs.
///
/// Resolution: the user's saved setting (config.json <c>car_mode_end_phrase</c>), or <see cref="Default"/>
/// when unset/blank. Read at turn time so a change applies to the next turn.
/// </summary>
public static class CarModeEndPhraseConfig
{
    /// <summary>The config.json key the chosen end phrase is stored under.</summary>
    public const string ConfigKey = "car_mode_end_phrase";

    /// <summary>The default sign-off phrase, the one proven reliable through the Gateway transcription.</summary>
    public const string Default = "over and out";

    /// <summary>
    /// The user's saved Car Mode end phrase (config.json <c>car_mode_end_phrase</c>), or
    /// <see cref="Default"/> when unset/blank.
    /// </summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var phrase = v.GetValue<string>().Trim();
            if (phrase.Length > 0) return phrase;
        }
        return Default;
    }

    /// <summary>Persist the chosen end phrase to config.json (merge-patch). A blank phrase resets to default
    /// (an empty end phrase would end every turn, so it is never stored).</summary>
    public static void Set(string phrase)
    {
        var trimmed = (phrase ?? string.Empty).Trim();
        CcDirectorConfigService.MergePatch(new JsonObject { [ConfigKey] = trimmed.Length > 0 ? trimmed : Default });
    }
}
