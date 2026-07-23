namespace CcDirector.Gateway.Settings;

/// <summary>
/// The fixed set of per-tenant setting keys stored in the <c>tenant_settings</c> table (issue #2017). Each
/// key names one overridable setting; the string value matches the existing process-global <c>config.json</c>
/// key so the mapping between a tenant override and the operator global default it falls back to is obvious at
/// a glance. These are the ONLY keys the typed <see cref="TenantSettingsResolver"/> reads and writes - a value
/// under any other key is not a setting this resolver serves.
///
/// The set is deliberately the values a tenant can genuinely choose for itself. It does NOT include the
/// machine-scoped settings (network addressing, autostart, brain restart, diagnostics), which stay self-host
/// only and keep their hosted deny, nor the "included" hosted facts (provider, transcription endpoint) that
/// have exactly one hosted option.
/// </summary>
public static class TenantSettingKeys
{
    /// <summary>The wingman's main reasoning model (global default: <c>brain_model</c>).</summary>
    public const string WingmanModel = "brain_model";

    /// <summary>The wingman's quick-turn model (global default: <c>brain_model_fast</c>).</summary>
    public const string WingmanFastModel = "brain_model_fast";

    /// <summary>The text-to-speech engine (global default: <c>tts_model</c>).</summary>
    public const string TtsModel = "tts_model";

    /// <summary>The voice the text-to-speech engine uses (global default: <c>tts_voice</c>).</summary>
    public const string TtsVoice = "tts_voice";

    /// <summary>The conversational model Car Mode drives (global default: <c>car_mode_model</c>).</summary>
    public const string CarModeModel = "car_mode_model";

    /// <summary>The spoken phrase that ends a Car Mode turn (global default: <c>car_mode_end_phrase</c>).</summary>
    public const string CarModeEndPhrase = "car_mode_end_phrase";

    /// <summary>The snooze lengths every Snooze menu offers, as the serialized presets list (global default:
    /// <c>snooze_presets</c>).</summary>
    public const string SnoozePresets = "snooze_presets";

    /// <summary>The default snooze length in minutes (global default: <c>snooze_default_minutes</c>).</summary>
    public const string SnoozeDefaultMinutes = "snooze_default_minutes";

    /// <summary>The display time zone (IANA id) the dashboards read local hours in (global default:
    /// <c>time_zone</c>).</summary>
    public const string TimeZone = "time_zone";

    /// <summary>The injected agent-launch text choice - the "use yours" flag and the user's own text -
    /// stored as one JSON object so the null-vs-empty distinction survives (global default:
    /// <c>injected_text</c>). The resolver owns serializing <c>InjectedTextSettings</c> to and from this
    /// string.</summary>
    public const string InjectedText = "injected_text";

    /// <summary>Every key this resolver serves, for validation and enumeration.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WingmanModel, WingmanFastModel, TtsModel, TtsVoice,
        CarModeModel, CarModeEndPhrase, SnoozePresets, SnoozeDefaultMinutes, TimeZone, InjectedText,
    };
}
