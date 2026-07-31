using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether this Director records every session's terminal screen to disk.
///
/// The recorder writes each session's resolved grid - every screen the agent has drawn, which is
/// every prompt, path, file name and secret that has been on it - to an append-only file under
/// <c>session-recordings/</c>. It exists to build a ground-truth corpus for OFFLINE analysis of our
/// own state detection, which is an internal engineering need and not something a user asked for.
///
/// It was ON by default, with the only way to turn it off being an environment variable named in a
/// source comment. That combination is what makes it a defect rather than a feature: an internal
/// analysis corpus, collected from every install, invisible in the product, with no age limit. The
/// default is now OFF, and turning it on is a visible decision in config.json:
///
///   "session_recording": { "enabled": true }
///
/// <c>CC_DIRECTOR_RECORD_SESSIONS</c> keeps working as an override in BOTH directions, so a machine
/// already set up for corpus collection does not have to be re-taught and the setting can be flipped
/// for one run without editing a file.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than silently
/// picking a default (matching <see cref="AutoResumeConfig"/>).
/// </summary>
public sealed record SessionRecordingConfig(bool Enabled)
{
    /// <summary>The default posture: not recording.</summary>
    public static readonly SessionRecordingConfig Default = new(Enabled: false);

    /// <summary>The environment override, honoured in both directions.</summary>
    public const string EnvironmentVariable = "CC_DIRECTOR_RECORD_SESSIONS";

    /// <summary>The config.json section this reads.</summary>
    public const string SectionName = "session_recording";

    /// <summary>
    /// The effective answer for this machine: the environment override if it says anything, then
    /// config.json, then off.
    /// </summary>
    public static bool IsEnabled()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var value = env.Trim();
            if (value is "0" or "false" or "False" or "FALSE" or "no" or "off") return false;
            if (value is "1" or "true" or "True" or "TRUE" or "yes" or "on") return true;

            throw new InvalidOperationException(
                $"The environment variable {EnvironmentVariable} must be 1 or 0 (or true/false), not '{value}'. "
                + $"Fix the value or unset it to use config.json's {SectionName}.enabled.");
        }

        return Get().Enabled;
    }

    /// <summary>Read the effective config from config.json's section; a missing key means off.</summary>
    public static SessionRecordingConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[SectionName];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"config.json key '{SectionName}' must be an object. "
                + "Fix the value or remove the key to use the default (not recording).");

        return new SessionRecordingConfig(Enabled: ReadBool(obj, "enabled", Default.Enabled));
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key '{SectionName}.{key}' must be true or false. "
            + "Fix the value or remove the key to use the default.");
    }
}
