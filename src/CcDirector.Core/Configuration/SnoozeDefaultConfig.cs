using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The GATEWAY-OWNED, per-user default snooze length, in whole minutes (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). Snoozing a session always holds it for
/// this one length - there is no per-snooze duration. Because every one of the user's devices talks
/// to their one Gateway, this single Gateway setting IS "the same snooze length across all devices".
///
/// Persisted in <c>config.json</c> as the top-level integer key <c>snooze_default_minutes</c>, the
/// same store the other Gateway settings use (<see cref="TelemetryConsentConfig"/>,
/// <see cref="AddressingModeConfig"/>). Default 60 (one hour): a Gateway with no persisted value
/// snoozes for one hour. Read at the moment a snooze is set, so a change takes effect on the next
/// snooze - no Gateway restart.
/// </summary>
public static class SnoozeDefaultConfig
{
    /// <summary>The config.json top-level key holding the per-user default snooze length in minutes.</summary>
    public const string Key = "snooze_default_minutes";

    /// <summary>The default when no value has ever been persisted: 60 minutes (one hour).</summary>
    public const int Default = 60;

    /// <summary>The smallest snooze length a caller may persist. One minute is the shortest useful
    /// hold and is exactly the value the Phase 1 live round-trip cranks the default down to.</summary>
    public const int MinMinutes = 1;

    /// <summary>The largest snooze length a caller may persist: 7 days, a generous ceiling that still
    /// keeps an accidental huge value (which would defeat the always-comes-back guarantee) out.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>
    /// Returns the per-user default snooze length in minutes. Defaults to <see cref="Default"/> (60)
    /// when no value has ever been persisted, and reads the persisted integer otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>snooze_default_minutes</c> key is present but is not a positive JSON integer.
    /// </exception>
    public static int Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[Key];
        if (node is null)
        {
            FileLog.Write("[SnoozeDefaultConfig] Get: no persisted value -> default 60 minutes");
            return Default;
        }

        if (node is JsonValue v && v.TryGetValue<int>(out var minutes) && minutes >= MinMinutes)
        {
            FileLog.Write($"[SnoozeDefaultConfig] Get: persisted value minutes={minutes}");
            return minutes;
        }

        throw new InvalidOperationException(
            "config.json key 'snooze_default_minutes' must be a positive whole number of minutes. " +
            "Fix the value or remove the key to use the default (60 = one hour).");
    }

    /// <summary>
    /// True when <paramref name="minutes"/> is a length this setting will accept: at least
    /// <see cref="MinMinutes"/> and at most <see cref="MaxMinutes"/>. Pure, so the endpoint can
    /// validate the request body before writing anything.
    /// </summary>
    public static bool IsValid(int minutes) => minutes >= MinMinutes && minutes <= MaxMinutes;

    /// <summary>
    /// Persists the per-user default snooze length to <c>config.json</c> under
    /// <c>snooze_default_minutes</c>, merging into the existing file so no other section is dropped.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minutes"/> is outside <see cref="MinMinutes"/>..<see cref="MaxMinutes"/>.
    /// </exception>
    public static void Set(int minutes)
    {
        if (!IsValid(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes,
                $"snooze default must be between {MinMinutes} and {MaxMinutes} minutes");

        FileLog.Write($"[SnoozeDefaultConfig] Set: minutes={minutes}");
        CcDirectorConfigService.MergePatch(new JsonObject { [Key] = minutes });
        FileLog.Write("[SnoozeDefaultConfig] Set: persisted");
    }
}
