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
    // The settled answer, for the two fixed states. Null in the deferred state, where the answer is asked
    // for rather than stored - see Deferred below.
    private readonly GatewayInputStatsAggregator? _settledAggregator;
    private readonly string? _settledReason;
    private readonly LateStatsObservers? _late;

    private InputStatsHandle(GatewayInputStatsAggregator? aggregator, string? unavailableReason,
        LateStatsObservers? late)
    {
        _settledAggregator = aggregator;
        _settledReason = unavailableReason;
        _late = late;
    }

    /// <summary>
    /// The aggregator, or null when statistics are unavailable.
    ///
    /// READ IT EVERY TIME; DO NOT CACHE THE RESULT IN A FIELD. In the deferred state this is a question,
    /// not a stored value: a hosted store whose PostgreSQL open ran past the startup deadline publishes its
    /// factory later, and this property is where that late arrival becomes visible. A caller that reads it
    /// once at construction and keeps the answer reintroduces exactly the defect the deferred state exists
    /// to fix.
    /// </summary>
    public GatewayInputStatsAggregator? Aggregator => _late is not null ? _late.Aggregator : _settledAggregator;

    /// <summary>Why there is no aggregator, or null when there is one. Operator-facing, credential-free.</summary>
    public string? UnavailableReason => _late is not null
        ? (_late.Aggregator is null ? _late.Reason : null)
        : _settledReason;

    /// <summary>Whether statistics are being recorded and served. Re-asked on every call, like
    /// <see cref="Aggregator"/>.</summary>
    public bool IsAvailable => Aggregator is not null;

    /// <summary>Statistics are available, on this aggregator.</summary>
    public static InputStatsHandle Available(GatewayInputStatsAggregator aggregator) =>
        new(aggregator, null, null);

    /// <summary>Statistics are unavailable, for this named reason, and that will not change while this
    /// process runs - a self-host file that could not be opened, or a hosted deployment with no statistics
    /// connection configured at all.</summary>
    public static InputStatsHandle Unavailable(string reason) =>
        new(null, reason, null);

    /// <summary>
    /// The answer is NOT YET KNOWN and must be asked for each time. Used on hosted, where the statistics
    /// store is allowed to publish its context factory after the startup deadline has passed; see
    /// <see cref="LateStatsObservers"/> for why freezing the answer at construction was a defect.
    /// </summary>
    public static InputStatsHandle Deferred(LateStatsObservers late) =>
        new(null, null, late ?? throw new ArgumentNullException(nameof(late)));
}
