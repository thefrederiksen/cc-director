namespace CcDirector.Core.Configuration;

/// <summary>
/// Turning a snooze length in minutes into the words a menu row shows: "15 minutes", "1 hour",
/// "4 hours", "1 hour 30 minutes", "2 days".
///
/// This is the C# twin of <c>packages/client-core/src/settings/snoozeFormat.ts</c>. The wording is
/// duplicated across the two languages because the desktop is C# and the Cockpit and phone are
/// TypeScript, and there is no shared runtime between them - but it MUST stay identical, because
/// "4 hours" on the desktop and "240 minutes" on the phone would read as two different settings when
/// they are one Gateway-owned value. SnoozeLengthTextTests pins the shipped lengths to the exact strings
/// the TypeScript tests pin, so a change to one side without the other fails the build.
/// </summary>
public static class SnoozeLengthText
{
    private const int MinutesPerHour = 60;
    private const int MinutesPerDay = 24 * 60;

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// The words for a snooze length. Whole units read as one word; a length that does not divide evenly
    /// reads as the larger unit plus the remainder, so nothing is rounded away and the row always names
    /// the exact length it sets.
    /// </summary>
    public static string Format(int minutes)
    {
        if (minutes < 1) return $"{minutes} minutes";

        if (minutes < MinutesPerHour) return Plural(minutes, "minute");

        if (minutes < MinutesPerDay)
        {
            var hours = minutes / MinutesPerHour;
            var rest = minutes % MinutesPerHour;
            return rest == 0 ? Plural(hours, "hour") : $"{Plural(hours, "hour")} {Plural(rest, "minute")}";
        }

        var days = minutes / MinutesPerDay;
        var restMinutes = minutes % MinutesPerDay;
        if (restMinutes == 0) return Plural(days, "day");

        var restHours = (int)Math.Round(restMinutes / (double)MinutesPerHour, MidpointRounding.AwayFromZero);
        // A remainder under half an hour would render as "1 day 0 hours", which reads as a bug. Name the
        // exact minutes instead - these lengths are rare and being exact matters more than being short.
        if (restHours == 0) return $"{Plural(days, "day")} {Plural(restMinutes, "minute")}";
        return $"{Plural(days, "day")} {Plural(restHours, "hour")}";
    }
}
