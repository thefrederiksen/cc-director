namespace CcDirector.Gateway.Stats;

/// <summary>
/// The input-statistics aggregator, or the NAMED reason there is not one.
///
/// This type exists so that "there are no statistics" is a state the Gateway can be in and still serve.
/// The aggregator opens a database in its constructor; before this handle existed, a Gateway that could
/// not open that database did not start at all - which is how a hosted deploy came down with
/// "SQLite Error 14: unable to open database file" and never reached the roster or the tunnels. The
/// roster and the tunnels do not depend on statistics, so a statistics failure must not be able to take
/// them with it.
///
/// It is ALWAYS registered in the container, present or absent, so that everything which folds into
/// statistics (the DirectorHub above all) can be constructed either way. A consumer reads
/// <see cref="Aggregator"/> and does nothing when it is null; it never has to know why.
///
/// The reason is carried rather than discarded because an absent statistics surface with no explanation
/// is indistinguishable from a broken one. <see cref="UnavailableReason"/> is written into the log at
/// startup and is safe to put on a health surface - it never carries a connection string.
/// </summary>
public sealed class InputStatsHandle
{
    private InputStatsHandle(GatewayInputStatsAggregator? aggregator, string? unavailableReason)
    {
        Aggregator = aggregator;
        UnavailableReason = unavailableReason;
    }

    /// <summary>The aggregator, or null when statistics are unavailable.</summary>
    public GatewayInputStatsAggregator? Aggregator { get; }

    /// <summary>Why there is no aggregator, or null when there is one. Operator-facing, credential-free.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Whether statistics are being recorded and served.</summary>
    public bool IsAvailable => Aggregator is not null;

    /// <summary>Statistics are available, on this aggregator.</summary>
    public static InputStatsHandle Available(GatewayInputStatsAggregator aggregator) =>
        new(aggregator, null);

    /// <summary>Statistics are unavailable, for this named reason.</summary>
    public static InputStatsHandle Unavailable(string reason) =>
        new(null, reason);
}
