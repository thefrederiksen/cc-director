namespace CcDirector.Gateway.Settings;

/// <summary>
/// How often an account wants the daily report email (issue #1000, follow-up to the wizard removal in
/// devthrottle_internal#996).
///
/// WHY THIS IS AN ENUM AND NOT A BOOLEAN. The question is "how often", and it has a third answer already
/// waiting: weekly, once <c>MorningReportBuilder</c> can summarize a range instead of one calendar day. A
/// boolean would have to become a name to admit that answer, rewriting every stored row; a name absorbs it.
/// Weekly is deliberately NOT a member yet - a member here is a promise the sender can keep, and today it
/// could only mail one day's report on a Monday and call it a week.
/// </summary>
public enum ReportCadence
{
    /// <summary>One email every morning. The default, and what every account got before this setting existed.</summary>
    Daily,

    /// <summary>No report email at all. The account said so; it is not the absence of a choice.</summary>
    Off,
}

/// <summary>
/// The stored names for <see cref="ReportCadence"/> and the one place they are parsed and written.
///
/// The names are the wire contract in three places at once - the <c>tenant_settings</c> row, the settings
/// snapshot the cockpit and phone render, and the body of the write - so they are defined once here rather
/// than spelled out at each end where two of them could drift apart.
/// </summary>
public static class ReportCadences
{
    /// <summary>The stored/wire name for <see cref="ReportCadence.Daily"/>.</summary>
    public const string DailyName = "daily";

    /// <summary>The stored/wire name for <see cref="ReportCadence.Off"/>.</summary>
    public const string OffName = "off";

    /// <summary>
    /// What an account gets when it has never touched this setting: the report, every day. This is the
    /// pre-existing behaviour stated as a constant, so turning the setting on for the first time cannot
    /// silently change what anybody already receives.
    /// </summary>
    public const ReportCadence Default = ReportCadence.Daily;

    /// <summary>Every name a caller may write, for validation and for naming them in an error message.</summary>
    public static readonly IReadOnlyList<string> AllNames = new[] { DailyName, OffName };

    /// <summary>The stored name for a cadence.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The cadence is not a known member.</exception>
    public static string Name(ReportCadence cadence) => cadence switch
    {
        ReportCadence.Daily => DailyName,
        ReportCadence.Off => OffName,
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown report cadence"),
    };

    /// <summary>
    /// Parse a stored or submitted name. False for anything unrecognized, so the caller decides what an
    /// unreadable value means rather than inheriting a lenient parse's guess.
    /// </summary>
    public static bool TryParse(string? raw, out ReportCadence cadence)
    {
        cadence = Default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case DailyName: cadence = ReportCadence.Daily; return true;
            case OffName: cadence = ReportCadence.Off; return true;
            default: return false;
        }
    }
}
