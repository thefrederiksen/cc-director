namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One member of one hour's distinct set (<c>concurrency_hour_member</c>): a session id, a machine name or a
/// repository path that was seen during that hour, for that tenant. The whole row IS the key - there is
/// nothing else to store.
///
/// WHAT THIS TABLE IS FOR, because it is easy to mistake it for the source of the distinct counts and it is
/// not. The counts on <c>concurrency_hour</c> are produced by three in-memory <c>HashSet</c>s with the
/// comparers the fleet has always used: <c>StringComparer.Ordinal</c> for session ids (an id is an exact
/// token - two ids differing in case are two different sessions) and
/// <c>StringComparer.OrdinalIgnoreCase</c> for machine names and repository paths (one machine reported as
/// "SOREN_NORTH" and "Soren_North" is one machine, and Windows paths are not case-sensitive). This table is
/// ONLY how those sets survive a process restart: the raw strings are written here and read straight back
/// into the same HashSets, which is exactly what the JSON store's Load did.
///
/// THE CONSEQUENCE, WRITTEN DOWN SO NOBODY "FIXES" IT. This table's key is ordinal on every provider, so it
/// can legally hold BOTH "SOREN_NORTH" and "Soren_North" as two rows of kind <c>machine</c>. That is
/// HARMLESS, not a bug: the rows are never counted and never compared by the database - they are rehydrated
/// into the OrdinalIgnoreCase HashSet, which collapses them back to one machine, and the count the page
/// shows comes from that set. Do NOT reach for a case-insensitive column, a citext type or a lower(...)
/// unique index to "clean this up". Doing so would make the DATABASE the authority on whether two machine
/// names are the same thing, under whatever collation it happens to run - and the database's answer and the
/// HashSet's answer would then be free to differ. This is the same reasoning written out at length on
/// <c>repo_identity</c> in <c>GatewayStatsDatabase.MigrateToVersion1</c>: the only component allowed to
/// decide identity is the comparer that decides it today.
///
/// LIFETIME: ONE HOUR PER TENANT, NOT NINETY DAYS OF THEM. The dedup sets belong to the CURRENT hour, and
/// the file store held exactly one hour's worth - three lists beside a single current-hour key - clearing
/// them whenever the observed hour differed from that key, in either direction of travel. So the moment a
/// tenant's hour changes, its rows for every other hour are discarded here too. Keeping them and unioning
/// them across a returning hour would report a higher distinct count than the file store did, which is a
/// better answer and the wrong one for a port. The ninety-day prune still runs over this table as well, and
/// it is not redundant: it is what eventually clears the last hour of a tenant that stopped being observed.
/// </summary>
public sealed class ConcurrencyHourMemberEntity
{
    /// <summary>The owning tenant (the raw <see cref="Core.Tenancy.TenantId.Value"/>). Part of the key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The UTC clock hour this member was seen in, formatted <c>yyyy-MM-ddTHH</c>. Part of the key,
    /// and what retention prunes on.</summary>
    public string HourUtc { get; set; } = "";

    /// <summary>Which set this row belongs to: <c>session</c>, <c>machine</c> or <c>repo</c> (see
    /// <see cref="ConcurrencyMemberKinds"/>). Part of the key.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The RAW string as the roster reported it - a session id, a machine name or a repository
    /// path. Never normalized, never case-folded. Part of the key.</summary>
    public string MemberId { get; set; } = "";
}

/// <summary>
/// The three sets <see cref="ConcurrencyHourMemberEntity.Kind"/> can name. Constants rather than an enum
/// because the value is stored as text and read back as text: the stored spelling is part of the schema, so
/// it is written down once here instead of being derived from a C# member name that a rename could change.
/// </summary>
public static class ConcurrencyMemberKinds
{
    /// <summary>Session ids seen in the hour. Deduped with <c>StringComparer.Ordinal</c>.</summary>
    public const string Session = "session";

    /// <summary>Machine names seen in the hour. Deduped with <c>StringComparer.OrdinalIgnoreCase</c>.</summary>
    public const string Machine = "machine";

    /// <summary>Repository paths seen in the hour. Deduped with <c>StringComparer.OrdinalIgnoreCase</c>.</summary>
    public const string Repo = "repo";
}
