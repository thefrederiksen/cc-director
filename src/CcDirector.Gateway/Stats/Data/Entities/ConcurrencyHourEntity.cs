namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One tenant's activity log for one UTC clock hour (<c>concurrency_hour</c>): the maximum concurrency seen
/// in that hour (the 24-hour chart's two series) and how many DISTINCT sessions, machines and repositories
/// were seen in it (the "how much ran" totals). The weekly maximum on the statistics page is derived from
/// these rows, never stored.
///
/// All five value columns are MAXIMA and only ever grow, so all five are written with an explicit
/// <c>ON CONFLICT DO UPDATE ... GREATEST</c> - never a change-tracked read-then-save. The distinct counts
/// grow because they are the size of a set that only gains members within an hour.
///
/// <see cref="HourUtc"/> is a STRING in the format <c>yyyy-MM-ddTHH</c>, not a timestamp. It is the same key
/// the JSON store used and the same key every other table in this statistics store uses for an hour. Because
/// the format is fixed-width and zero-padded, ordering it as text is the same order as ordering it as time,
/// which is what lets retention prune with a plain range predicate.
/// </summary>
public sealed class ConcurrencyHourEntity
{
    /// <summary>The owning tenant (the raw <see cref="Core.Tenancy.TenantId.Value"/>). Part of the key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The UTC clock hour, formatted <c>yyyy-MM-ddTHH</c>. Part of the key.</summary>
    public string HourUtc { get; set; } = "";

    /// <summary>The most live (non-exited) sessions seen at once during this hour.</summary>
    public int MaxLive { get; set; }

    /// <summary>The most sessions seen working at once during this hour.</summary>
    public int MaxWorking { get; set; }

    /// <summary>How many distinct sessions were seen during this hour.</summary>
    public int DistinctSessions { get; set; }

    /// <summary>How many distinct machines were seen during this hour.</summary>
    public int DistinctMachines { get; set; }

    /// <summary>How many distinct repositories were seen during this hour.</summary>
    public int DistinctRepos { get; set; }
}
