using Cronos;
using CcDirector.Core.Utilities;

namespace CcDirector.Engine.Scheduling;

public static class CronHelper
{
    public static bool IsValid(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            CronExpression.Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    public static DateTime? GetNextOccurrence(string expression, DateTime from)
    {
        var cron = CronExpression.Parse(expression);
        return cron.GetNextOccurrence(from, inclusive: false);
    }

    public static DateTime? GetNextOccurrenceUtc(string expression)
    {
        return GetNextOccurrence(expression, DateTime.UtcNow);
    }

    public static string Describe(string expression)
    {
        try
        {
            var cron = CronExpression.Parse(expression);
            var next = cron.GetNextOccurrence(DateTime.UtcNow);
            return next.HasValue
                ? $"Next: {next.Value:yyyy-MM-dd HH:mm:ss} UTC"
                : "No upcoming occurrence";
        }
        catch (CronFormatException)
        {
            return "Invalid cron expression";
        }
    }
}
