using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// The ONE typed per-tenant settings resolver (issue #2017) - the seam the MTR runtime-threading follow-up
/// builds on. Every method REQUIRES an explicit <see cref="TenantId"/>: this layer performs no ambient or
/// static tenant inference. For each setting it returns the tenant's own override when one is set and valid,
/// otherwise the OPERATOR GLOBAL DEFAULT (the existing process-global <c>config.json</c> value) - never another
/// tenant's value. A <see cref="TenantId"/> cannot be blank by construction, and on hosted a blank/unresolved
/// tenant is a route-level 403 before this resolver is reached, so a returned value can never be attributed to
/// the wrong tenant.
///
/// Two faces:
///   - READ methods (<see cref="WingmanModel"/> etc.) are what the runtime consumers call once MTR threads the
///     resolved tenant to their call sites (wingman brain, text-to-speech, car mode, snooze creation, stats).
///   - WRITE methods (<see cref="SetWingmanModel"/> etc.) are what the settings-page endpoints call; they
///     VALIDATE with the same rules the global setters use, then persist a per-tenant override.
///
/// Serialization is opaque to the <see cref="TenantSettingsStore"/> below it: this resolver owns turning a
/// typed value into the stored string and back, and owns the fallback when a stored string is missing or fails
/// validation (a corrupt override must never leak another tenant's value or crash a turn - it degrades to the
/// operator default, which is the safe, tenant-neutral answer).
/// </summary>
public sealed class TenantSettingsResolver
{
    private readonly TenantSettingsStore _store;

    /// <param name="store">The per-tenant override store. Required.</param>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public TenantSettingsResolver(TenantSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    // ---- reads: tenant override, else operator global default -------------------------------------------

    /// <summary>The tenant's wingman model for a role, or the operator global default when unset.</summary>
    public string WingmanModel(TenantId tenant, TranscriptionMode mode, WingmanModelRole role)
    {
        var key = role switch
        {
            WingmanModelRole.Thinking => TenantSettingKeys.WingmanModel,
            WingmanModelRole.Fast => TenantSettingKeys.WingmanFastModel,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown wingman model role"),
        };
        return NonEmptyOverride(tenant, key) ?? WingmanModelConfig.Resolve(mode, role);
    }

    /// <summary>The tenant's text-to-speech voice, or the operator global default when unset.</summary>
    public string TtsVoice(TenantId tenant, TranscriptionMode mode)
        => NonEmptyOverride(tenant, TenantSettingKeys.TtsVoice) ?? TtsVoiceConfig.Resolve(mode);

    /// <summary>The tenant's text-to-speech model, or the operator global default when unset.</summary>
    public string TtsModel(TenantId tenant, TranscriptionMode mode)
        => NonEmptyOverride(tenant, TenantSettingKeys.TtsModel) ?? TtsModelConfig.Resolve(mode);

    /// <summary>The tenant's Car Mode model, or the operator global default when unset.</summary>
    public string CarModeModel(TenantId tenant)
        => NonEmptyOverride(tenant, TenantSettingKeys.CarModeModel) ?? CarModeModelConfig.Resolve();

    /// <summary>The tenant's Car Mode end phrase, or the operator global default when unset.</summary>
    public string CarModeEndPhrase(TenantId tenant)
        => NonEmptyOverride(tenant, TenantSettingKeys.CarModeEndPhrase) ?? CarModeEndPhraseConfig.Get();

    /// <summary>The tenant's default snooze length in minutes, or the operator global default when unset or
    /// when the stored override fails validation.</summary>
    public int SnoozeDefaultMinutes(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.SnoozeDefaultMinutes);
        if (raw is not null && int.TryParse(raw, out var minutes) && SnoozeDefaultConfig.IsValid(minutes))
            return minutes;
        return SnoozeDefaultConfig.Get();
    }

    /// <summary>The tenant's snooze presets list (ascending), or the operator global default when unset or when
    /// the stored override fails validation as a set with its default.</summary>
    public IReadOnlyList<int> SnoozePresets(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.SnoozePresets);
        var parsed = ParsePresets(raw);
        if (parsed is not null)
        {
            var def = SnoozeDefaultMinutes(tenant);
            if (SnoozePresetsConfig.IsValidSet(parsed, def, out _))
                return parsed;
        }
        return SnoozePresetsConfig.Get();
    }

    /// <summary>The tenant's display time zone (IANA id), or the operator global default when unset or invalid.</summary>
    public string TimeZone(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.TimeZone);
        if (raw is not null && TimeZoneConfig.IsValid(raw))
            return raw;
        return TimeZoneConfig.Get();
    }

    // ---- writes: validate like the global setters, then persist a per-tenant override -------------------

    /// <summary>Set the tenant's wingman model for a role.</summary>
    /// <exception cref="ArgumentException">The model is null/empty.</exception>
    public void SetWingmanModel(TenantId tenant, WingmanModelRole role, string model, DateTime nowUtc)
    {
        var trimmed = RequireNonEmpty(model, nameof(model));
        var key = role switch
        {
            WingmanModelRole.Thinking => TenantSettingKeys.WingmanModel,
            WingmanModelRole.Fast => TenantSettingKeys.WingmanFastModel,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown wingman model role"),
        };
        _store.Set(tenant, key, trimmed, nowUtc);
    }

    /// <summary>Set the tenant's text-to-speech voice.</summary>
    /// <exception cref="ArgumentException">The voice is null/empty.</exception>
    public void SetTtsVoice(TenantId tenant, string voice, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TtsVoice, RequireNonEmpty(voice, nameof(voice)), nowUtc);

    /// <summary>Set the tenant's text-to-speech model.</summary>
    /// <exception cref="ArgumentException">The model is null/empty.</exception>
    public void SetTtsModel(TenantId tenant, string model, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TtsModel, RequireNonEmpty(model, nameof(model)), nowUtc);

    /// <summary>Set the tenant's Car Mode model.</summary>
    /// <exception cref="ArgumentException">The model is null/empty.</exception>
    public void SetCarModeModel(TenantId tenant, string model, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.CarModeModel, RequireNonEmpty(model, nameof(model)), nowUtc);

    /// <summary>Set the tenant's Car Mode end phrase. A blank phrase RESETS this tenant to the operator
    /// default by CLEARING the override (an empty phrase would end every turn), mirroring the global setter's
    /// blank-resets-to-default behaviour - per tenant.</summary>
    public void SetCarModeEndPhrase(TenantId tenant, string phrase, DateTime nowUtc)
    {
        var trimmed = (phrase ?? "").Trim();
        if (trimmed.Length == 0)
            _store.Remove(tenant, TenantSettingKeys.CarModeEndPhrase);
        else
            _store.Set(tenant, TenantSettingKeys.CarModeEndPhrase, trimmed, nowUtc);
    }

    /// <summary>Set the tenant's display time zone (an IANA id).</summary>
    /// <exception cref="ArgumentException">The id is not a valid IANA time-zone id.</exception>
    public void SetTimeZone(TenantId tenant, string timeZone, DateTime nowUtc)
    {
        if (!TimeZoneConfig.IsValid(timeZone))
            throw new ArgumentException($"'{timeZone}' is not a valid time zone id.", nameof(timeZone));
        _store.Set(tenant, TenantSettingKeys.TimeZone, timeZone, nowUtc);
    }

    /// <summary>Reset this tenant's AI model/voice choices to the operator defaults by CLEARING those
    /// overrides (the legacy "reset to provider defaults" action). Snooze, time zone, and car-mode settings are
    /// left untouched.</summary>
    public void ClearAiProviderOverrides(TenantId tenant)
    {
        _store.Remove(tenant, TenantSettingKeys.WingmanModel);
        _store.Remove(tenant, TenantSettingKeys.WingmanFastModel);
        _store.Remove(tenant, TenantSettingKeys.TtsModel);
        _store.Remove(tenant, TenantSettingKeys.TtsVoice);
    }

    /// <summary>Set the tenant's snooze presets and default together, holding the invariant that the default is
    /// one of the presets (the same invariant the global setter enforces).</summary>
    /// <exception cref="ArgumentException">The presets/default are not a valid set.</exception>
    public void SetSnoozePresets(TenantId tenant, IReadOnlyList<int> presets, int defaultMinutes, DateTime nowUtc)
    {
        if (!SnoozePresetsConfig.IsValidSet(presets, defaultMinutes, out var error))
            throw new ArgumentException(error, nameof(presets));
        var sorted = presets.OrderBy(m => m).ToList();
        // Persist both, so a read of either is self-consistent for this tenant.
        _store.Set(tenant, TenantSettingKeys.SnoozePresets, SerializePresets(sorted), nowUtc);
        _store.Set(tenant, TenantSettingKeys.SnoozeDefaultMinutes, defaultMinutes.ToString(), nowUtc);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>An override value only when it is present AND non-empty; null otherwise (so the caller falls
    /// back to the operator global default rather than to an empty string).</summary>
    private string? NonEmptyOverride(TenantId tenant, string key)
    {
        var v = _store.Get(tenant, key);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Presets are stored as ascending comma-joined integers, e.g. "15,60,240,480".</summary>
    internal static string SerializePresets(IReadOnlyList<int> presets)
        => string.Join(",", presets);

    /// <summary>Parse the stored presets string back to a list, or null when absent or malformed (so the caller
    /// falls back to the operator default rather than a partial list).</summary>
    internal static IReadOnlyList<int>? ParsePresets(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n)) return null;
            list.Add(n);
        }
        return list.Count == 0 ? null : list;
    }

    private static string RequireNonEmpty(string value, string paramName)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException($"{paramName} is required.", paramName);
        return trimmed;
    }
}
