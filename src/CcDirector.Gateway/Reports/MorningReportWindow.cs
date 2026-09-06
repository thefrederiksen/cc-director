using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Reports;

/// <summary>
/// Thrown when a morning-report request cannot be resolved into a window: an unparseable date, an unknown
/// IANA zone. The endpoint maps it to a 400 with the message, so a caller learns exactly what it got wrong
/// instead of receiving a report over a window nobody asked for.
/// </summary>
public sealed class MorningReportWindowException : Exception
{
    public MorningReportWindowException(string message) : base(message) { }
}

/// <summary>
/// Resolves "the calendar day <c>date</c> in zone <c>tz</c>" into the exact half-open UTC range
/// [<see cref="StartUtc"/>, <see cref="EndUtc"/>) the report measures over. Every number in a morning report
/// carries these coordinates, so the recipient can always check what "yesterday" meant.
///
/// DAYLIGHT SAVING IS HANDLED, NOT ASSUMED AWAY. The window is NOT "start plus 24 hours": the end is the
/// start of the NEXT calendar day resolved independently in the same zone, so a spring-forward day is 23
/// hours long and an autumn-back day is 25. Computing the end by adding a day to the UTC start would
/// silently shift the boundary by an hour twice a year - an hour of work landing in the wrong report.
///
/// A calendar day whose local midnight DOES NOT EXIST (zones that spring forward at midnight, e.g.
/// America/Santiago) starts at the first instant that day does exist. That is not a guess: it is the
/// definition of when the day begins there, and <see cref="TimeZoneInfo.IsInvalidTime"/> is what tells us.
/// </summary>
public sealed class MorningReportWindow
{
    /// <summary>The wire format for the <c>date</c> parameter.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>Inclusive start of the reported calendar day, in UTC.</summary>
    public DateTime StartUtc { get; }

    /// <summary>EXCLUSIVE end of the reported calendar day, in UTC.</summary>
    public DateTime EndUtc { get; }

    /// <summary>The calendar day, exactly as the caller supplied it.</summary>
    public string Date { get; }

    /// <summary>The zone identifier, exactly as the caller supplied it.</summary>
    public string Tz { get; }

    private MorningReportWindow(DateTime startUtc, DateTime endUtc, string date, string tz)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
        Date = date;
        Tz = tz;
    }

    /// <summary>
    /// Resolve a <paramref name="date"/> (yyyy-MM-dd) in <paramref name="tz"/> (an IANA zone id, e.g.
    /// "America/Toronto") into its UTC range.
    /// </summary>
    /// <exception cref="MorningReportWindowException">The date or the zone is not usable.</exception>
    public static MorningReportWindow Resolve(string? date, string? tz)
    {
        if (string.IsNullOrWhiteSpace(date))
            throw new MorningReportWindowException(
                $"A 'date' is required, in {DateFormat} form (the calendar day being reported on).");
        if (string.IsNullOrWhiteSpace(tz))
            throw new MorningReportWindowException(
                "A 'tz' is required - the IANA zone the calendar day is measured in (e.g. America/Toronto).");

        var dateText = date.Trim();
        var tzText = tz.Trim();

        if (!DateTime.TryParseExact(dateText, DateFormat, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
            throw new MorningReportWindowException($"'{dateText}' is not a date in {DateFormat} form.");

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(tzText);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new MorningReportWindowException($"'{tzText}' is not a time zone this Gateway knows.");
        }

        var startUtc = StartOfLocalDayUtc(day.Date, zone);
        var endUtc = StartOfLocalDayUtc(day.Date.AddDays(1), zone);

        if (endUtc <= startUtc)
            throw new MorningReportWindowException(
                $"The window for {dateText} in {tzText} resolved to a non-positive range; refusing to report over it.");

        FileLog.Write($"[MorningReportWindow] Resolve: date={dateText} tz={tzText} -> {startUtc:o}..{endUtc:o}");
        return new MorningReportWindow(startUtc, endUtc, dateText, tzText);
    }

    /// <summary>
    /// The UTC instant a local calendar day begins in <paramref name="zone"/>. Local midnight normally; the
    /// first instant that exists when the zone skips midnight (a spring-forward at 00:00). An AMBIGUOUS local
    /// midnight (an autumn-back that repeats 00:00) resolves to the EARLIER of the two instants, which is the
    /// one the day actually starts at - <see cref="TimeZoneInfo.ConvertTimeToUtc"/> resolves ambiguity to
    /// standard time, and standard time is the later offset, so the earlier instant is taken explicitly.
    /// </summary>
    /// <remarks>Internal rather than private because the Your Throttle week window
    /// (<see cref="Stats.StatsPageEndpoint"/>) resolves a Monday midnight in the caller's zone the same way,
    /// and two copies of a midnight rule diverge the moment one is corrected.</remarks>
    internal static DateTime StartOfLocalDayUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Midnight does not exist that day. Walk forward a minute at a time to the first instant that
            // does - a skipped span is at most a couple of hours, so this terminates quickly and needs no
            // assumption about how long the skip is.
            var probe = local;
            var limit = local.AddDays(1);
            while (zone.IsInvalidTime(probe) && probe < limit)
                probe = probe.AddMinutes(1);
            if (zone.IsInvalidTime(probe))
                throw new MorningReportWindowException(
                    $"No instant of {localDate:yyyy-MM-dd} exists in {zone.Id}; refusing to guess a window.");
            local = probe;
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The local time happens twice (an autumn-back). The day starts at the FIRST of them, which is
            // the one with the LARGEST UTC offset (daylight time, before the clocks go back).
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var earliest = offsets.Max();
            return DateTime.SpecifyKind(local - earliest, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }
}
