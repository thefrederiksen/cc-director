using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The GATEWAY-OWNED display time zone - an IANA zone id, e.g. "America/New_York" - that the owner's
/// private dashboards (the DevThrottle Stats "Your Throttle" hourly charts) render local clock hours in.
/// Because every one of the owner's devices talks to their one Gateway, this single Gateway setting IS
/// "the time zone my charts read in".
///
/// Persisted in <c>config.json</c> as the top-level string key <c>time_zone</c>, the same store the other
/// Gateway settings use (for example <see cref="SnoozeDefaultConfig"/>). When no
/// value has ever been persisted it AUTO-DEFAULTS to the Gateway machine's own zone
/// (<see cref="MachineDefault"/>), so the charts read local time out of the box with no setup; the owner
/// can override it on the Settings page. Read at render time, so a change takes effect on the next refresh
/// with no Gateway restart.
/// </summary>
public static class TimeZoneConfig
{
    /// <summary>The config.json top-level key holding the display time zone (an IANA zone id).</summary>
    public const string Key = "time_zone";

    /// <summary>
    /// The display time zone: the persisted IANA id if one is set, else the Gateway machine's own zone
    /// (the automatic default). Never returns an empty or invalid id.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>time_zone</c> key is present but is not a valid IANA zone id.
    /// </exception>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[Key];
        if (node is null)
        {
            var auto = MachineDefault();
            FileLog.Write($"[TimeZoneConfig] Get: no persisted value -> machine default {auto}");
            return auto;
        }

        if (node is JsonValue v && v.TryGetValue<string>(out var saved) && IsValid(saved))
        {
            FileLog.Write($"[TimeZoneConfig] Get: persisted value {saved!.Trim()}");
            return saved!.Trim();
        }

        throw new InvalidOperationException(
            "config.json key 'time_zone' must be a valid IANA time zone id (for example \"America/New_York\"). " +
            "Fix the value or remove the key to use the Gateway machine's own zone.");
    }

    /// <summary>
    /// The Gateway machine's own zone as an IANA id - the automatic default when nothing is persisted.
    /// On a host whose local zone is a Windows id (e.g. "Eastern Standard Time"), it is converted to the
    /// matching IANA id so a browser's Intl formatter can consume it. Falls back to "Etc/UTC" only when the
    /// machine's zone cannot be represented as IANA at all.
    /// </summary>
    public static string MachineDefault()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
            return local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) && !string.IsNullOrWhiteSpace(iana))
            return iana!;
        FileLog.Write($"[TimeZoneConfig] MachineDefault: local zone '{local.Id}' has no IANA mapping -> Etc/UTC");
        return "Etc/UTC";
    }

    /// <summary>
    /// True when <paramref name="id"/> is a zone id this host recognizes (IANA, or a Windows id the
    /// runtime accepts). Pure, so the endpoint can validate the request body before writing anything.
    /// </summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out _);
    }

    /// <summary>
    /// Persists the display time zone to <c>config.json</c> under <c>time_zone</c>, merging into the
    /// existing file so no other section is dropped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid zone id.</exception>
    public static void Set(string id)
    {
        if (!IsValid(id))
            throw new ArgumentException(
                "time zone must be a valid IANA zone id (for example \"America/New_York\")", nameof(id));

        var value = id.Trim();
        FileLog.Write($"[TimeZoneConfig] Set: timeZone={value}");
        CcDirectorConfigService.MergePatch(new JsonObject { [Key] = value });
        FileLog.Write("[TimeZoneConfig] Set: persisted");
    }
}
