namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// What the adoption step found when it looked at an existing SQLite statistics store, and what it did.
/// </summary>
public enum StatsStoreAdoptionOutcome
{
    /// <summary>There is nothing to adopt: no file on disk, or a file with no tables in it. The migration
    /// chain will create the schema from scratch, exactly as it does on a new machine.</summary>
    FreshStore,

    /// <summary>The store is already tracked by Entity Framework - it has a migration history table - so the
    /// chain reads that history and proceeds normally. Every store adopted once is in this state on every
    /// later startup, so this is the steady state, not an exception.</summary>
    AlreadyTracked,

    /// <summary>An untracked store at SQLite schema version 5 was ADOPTED: the history table was created and
    /// the baseline migration stamped as applied. The rows were already the right shape; the only thing
    /// missing was the bookkeeping that says so.</summary>
    Adopted,

    /// <summary>The store could not be adopted and the statistics surface is UNAVAILABLE. The Gateway keeps
    /// running and keeps serving; see <see cref="StatsStoreAdoptionResult.Reason"/> for which case it is.
    /// </summary>
    NotAdoptable,
}

/// <summary>
/// Why an existing statistics store could not be adopted. A NAMED reason, never a bare failure: these cases
/// present identically to an operator otherwise, and the next incident would be spent guessing which one it
/// was.
/// </summary>
public enum StatsStoreUnavailableReason
{
    /// <summary>No failure - the store is usable.</summary>
    None,

    /// <summary>A statistics store at a schema version this build cannot adopt. Either OLDER than version 5
    /// (whose forward migration is the hand-rolled path's job and is not repeated here) or NEWER (written by
    /// a build that knows something this one does not - opening it would be a downgrade against a shape this
    /// build does not know, which is the fastest way to lose the owner's numbers).</summary>
    IncompatibleSchemaVersion,

    /// <summary>The file has tables in it but they are not this store's. Adopting it would stamp a baseline
    /// over somebody else's database.</summary>
    NotAStatisticsStore,

    /// <summary>The file could not be read or interrogated at all - a locked, truncated or corrupt file, or
    /// a path that is not a readable database.</summary>
    StoreUnreadable,
}

/// <summary>
/// The result of the adoption step: whether the statistics store can be used, and if not, the named reason.
///
/// A RESULT rather than an exception, deliberately, and this is the shape the whole containment rule rests
/// on. A self-host user whose statistics file is in an unexpected state must get a statistics surface that
/// reports itself unavailable with a named reason and a Gateway that STILL STARTS and STILL SERVES ITS
/// ROSTER. This mission exists because a statistics fault took the primary read path down for 32 minutes on
/// the hosted Gateway; a version check that bricks a working desktop Gateway would be that same incident
/// repeated on the other surface. So nothing about a USER'S FILE throws - it is reported.
///
/// That is not a fallback and it must not decay into one: there is no substitute store, no alternative path
/// and no invented data. The statistics surface is simply off, loudly, with the reason named.
/// </summary>
/// <param name="Outcome">What the step found and did.</param>
/// <param name="Reason">The named reason when <see cref="Outcome"/> is
/// <see cref="StatsStoreAdoptionOutcome.NotAdoptable"/>; otherwise
/// <see cref="StatsStoreUnavailableReason.None"/>.</param>
/// <param name="Detail">A one-line operator-facing explanation, safe to log and to put on a failure surface.
/// Never contains credentials - a SQLite store is a local file path.</param>
public sealed record StatsStoreAdoptionResult(
    StatsStoreAdoptionOutcome Outcome,
    StatsStoreUnavailableReason Reason,
    string Detail)
{
    /// <summary>Whether the migration chain may now be run against this store. False means the statistics
    /// surface is unavailable with <see cref="Reason"/> and the Gateway carries on without it.</summary>
    public bool IsUsable => Outcome != StatsStoreAdoptionOutcome.NotAdoptable;
}
