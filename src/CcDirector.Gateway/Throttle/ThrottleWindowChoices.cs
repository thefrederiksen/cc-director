using System.Globalization;

namespace CcDirector.Gateway.Throttle;

/// <summary>
/// The lengths the Your Throttle period selector offers, and the Gateway's own name for each (mission
/// "Clean up Your Throttle", rulings R4 and R5). Decided HERE and served on every answer, so the clients
/// render the row of buttons they were handed and never keep a list of their own (CLAUDE.md rule 7).
///
/// The last choice is the ledger's retention (<see cref="ThrottleDefinition.RetentionDays"/>), never a number
/// typed here: the selector must never offer a length the store cannot honestly answer (#2692), and the one
/// place that knows how long the store keeps a submission is the retention sweep.
/// </summary>
public static class ThrottleWindowChoices
{
    /// <summary>The offered lengths in days, shortest first. One of them is the default
    /// (<see cref="ThrottleDefinition.DefaultWindowDays"/>) and the last is the retention.</summary>
    public static readonly IReadOnlyList<int> Days = new[] { 1, 7, 14, ThrottleDefinition.RetentionDays };

    /// <summary>The Gateway's label for a rolling window of <paramref name="days"/> days ending now.</summary>
    public static string Label(int days) => days == 1 ? "Last 24 hours" : $"Last {days} days";

    /// <summary>The choices as the feed serves them, in order.</summary>
    public static List<ThrottleWindowChoiceDto> Serve()
        => Days.Select(d => new ThrottleWindowChoiceDto { Days = d, Label = Label(d) }).ToList();

    /// <summary>The offered lengths as one plain list ("1, 7, 14, 30"), for the refusal that names them.</summary>
    public static string Named()
        => string.Join(", ", Days.Select(d => d.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>How a served window came to be: the four query forms <c>GET /stats/data</c> accepts.</summary>
public static class ThrottleWindowKinds
{
    /// <summary>No window was asked for: a rolling <see cref="ThrottleDefinition.DefaultWindowDays"/> days ending now.</summary>
    public const string Default = "default";

    /// <summary><c>days=N</c>: a rolling N days ending now, N one of the served choices.</summary>
    public const string Days = "days";

    /// <summary><c>week=YYYY-Www</c>: one ISO week, Monday to Monday in the caller's display zone.</summary>
    public const string Week = "week";

    /// <summary><c>from</c> and <c>to</c>: explicit UTC instants.</summary>
    public const string Explicit = "explicit";
}
