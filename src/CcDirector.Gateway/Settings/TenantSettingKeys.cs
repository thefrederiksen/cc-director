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

    /// <summary>Whether this tenant is IN VOICE MODE: every one of its sessions narrates its turns, including
    /// sessions created after the switch was thrown. Unlike every other key here there is no operator global
    /// default to fall back to - voice mode is a per-tenant choice and its default is OFF.
    ///
    /// This is the flag that makes voice mode a SWITCH rather than a one-shot fan-out. Before it, nothing
    /// anywhere held "this fleet is in voice mode": the phone inferred it by checking whether any session
    /// happened to be marked, and made it true by walking the roster - so a session created afterwards was
    /// never told, and quietly never joined the voice queue. The Gateway holds the intent here, and the
    /// sweep applies it to sessions as they appear.</summary>
    public const string VoiceModeAll = "voice_mode_all";

    /// <summary>
    /// Whether pending dictionary suggestions are mentioned in this tenant's daily report email. Stored as
    /// "true"/"false". Like <see cref="VoiceModeAll"/> there is no operator global default to fall back to -
    /// this is a per-tenant choice about a per-tenant email - and its default is ON, so the mention reaches the
    /// people who never open Settings, which is who the suggestions feature exists for.
    /// </summary>
    public const string DictationSuggestionsInDailyEmail = "dictation_suggestions_in_daily_email";

    /// <summary>
    /// The per-tenant state behind the daily email's "mention a batch at most twice" cadence, serialized as one
    /// JSON object so the batch identity and the send count stay consistent with each other. Not a setting the
    /// user edits - it is written by the email-block route and read by nothing else - but it lives here because
    /// it is exactly a small per-tenant value and needs no table of its own.
    /// </summary>
    public const string DictationEmailCadence = "dictation_email_cadence";

    /// <summary>
    /// How often this tenant wants the daily report email, stored as the cadence NAME (issue #1000). Like
    /// <see cref="VoiceModeAll"/> there is no operator global default to fall back to - it is one account's
    /// choice about one account's mail - and its default is every day, so nothing changes for anyone who
    /// never opens Settings.
    ///
    /// Stored as a NAME rather than a boolean on purpose. The question is "how often", and it already has a
    /// third answer waiting (weekly, once the report can summarize a range rather than one day). A boolean
    /// would have to be replaced by a name to admit that third answer, migrating every row already written;
    /// a name absorbs it as a new value.
    /// </summary>
    public const string DailyReportCadence = "daily_report_cadence";

    /// <summary>Every key this resolver serves, for validation and enumeration.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WingmanModel, WingmanFastModel, TtsModel, TtsVoice,
        CarModeModel, CarModeEndPhrase, SnoozePresets, SnoozeDefaultMinutes, TimeZone, InjectedText,
        VoiceModeAll, DictationSuggestionsInDailyEmail, DictationEmailCadence, DailyReportCadence,
    };
}
