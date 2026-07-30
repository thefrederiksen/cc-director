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
/// Why the statistics store is unavailable. A NAMED reason, never a bare failure: these cases present
/// identically to an operator otherwise, and the next incident would be spent guessing which one it was.
///
/// The first four are what the ADOPTION step can find in an existing self-host file. The last three are what
/// the STARTUP BOUNDARY can find, and the distinctions between those three are an Architect ruling rather
/// than a nicety.
///
/// THE RULE THE THREE OF THEM ENCODE, so that a fourth is added on the same grounds rather than on taste: a
/// named reason exists to separate causes that send a first responder to DIFFERENT PLACES. <see
/// cref="NotConfigured"/> is fixed by editing a setting, <see cref="Unreachable"/> is fixed by fixing a
/// database or a network, and <see cref="StoreSchemaIncomplete"/> is fixed on the store's own disk with both of
/// those already healthy. Collapsing any pair costs an incident spent looking in the wrong place, which is
/// the whole reason the distinction is worth a member.
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

    /// <summary>
    /// The store was written by a NEWER build: its migration history records migrations this build does not
    /// have.
    ///
    /// This is the desktop-rollback case, and refusing it is the whole point of the version discipline. An
    /// older build running against a newer store finds nothing pending - its chain is behind, not ahead - so
    /// it reports success and then fails on the first write that meets a constraint the newer build added,
    /// outside any containment. The hand-rolled code this replaces always refused a file newer than itself;
    /// this keeps that promise for stores the migration chain already tracks.
    ///
    /// The operator's action is specific and different from every other reason here: UPGRADE, do not repair.
    /// The store is not damaged - this build is simply behind it.
    /// </summary>
    StoreIsNewerThanThisBuild,

    /// <summary>
    /// Another process holds this store for writing, or an Entity Framework migration lock row was left
    /// behind by one that never finished.
    ///
    /// REFUSED FAST AND ON PURPOSE. Entity Framework's migration lock is acquired by retrying forever, with
    /// no timeout and no cancellation, and its row is removed on DISPOSAL - so a process that crashed while
    /// migrating leaves a row that nothing will ever clear. Any later open that reaches
    /// <c>Migrate()</c> then waits for ever on it.
    ///
    /// The containment boundary bounds that at twenty seconds, so startup survives - but it would burn the
    /// WHOLE deadline, on every single start, to learn what one query answers immediately. Worse, the wait
    /// is ABANDONED rather than cancelled, so each start leaks another thread blocked for the life of the
    /// process. Detecting the row up front turns a twenty-second stall into an instant, named refusal.
    ///
    /// The operator's action is the same either way: restart, and if it persists, a migration is stuck or its
    /// lock row was abandoned and must be cleared by hand. One state, one member.
    /// </summary>
    StoreLockedByAnotherProcess,

    /// <summary>
    /// THE STORE IS HALF-BUILT: it is ours, and it says so, but it is not in a state the chain can take
    /// forward. A table or column is absent, a name that should be a table is something else, or the tables
    /// are there while the migration history records nothing.
    ///
    /// THIS IS ONE REASON COVERING WHAT USED TO BE TWO, and the collapse is deliberate. The old pair -
    /// "schema incomplete" and "migration history incomplete" - named the two ROUTES by which the same state
    /// was noticed, not two different states. A reason named after its detection route ages badly the moment
    /// a second route finds the same state, which is exactly what happened: two different layers found the
    /// same half-built store within an afternoon and disagreed about what to call it.
    ///
    /// The test that settles it is the OPERATOR'S ACTION, and it is identical either way: this store cannot
    /// be taken forward automatically, so restore it from a backup or move it aside and start fresh. There
    /// is no safe automatic repair in either case - which half of an interrupted migration actually landed
    /// is a guess, and guessing it is how a store loses data quietly.
    ///
    /// WHICH ROUTE FOUND IT IS NOT LOST - it is in <see cref="StatsStoreAdoptionResult.Detail"/>, which names
    /// the missing tables or columns, or the fact that the history records nothing. The REASON names the
    /// state, so it stays a short stable set; the DETAIL names the mechanism, so the operator still learns
    /// exactly what was seen.
    /// </summary>
    StoreSchemaIncomplete,

    /// <summary>There is no statistics store CONFIGURED to open. A settings problem, and it is deliberately
    /// NOT the same reason as <see cref="Unreachable"/>: this one is fixed by setting an environment
    /// variable, and nobody should spend an incident looking at the network for it.
    ///
    /// It covers a self-host misconfiguration, an override that is SET BUT BLANK (a real operator error -
    /// somebody meant to name a database and left the value empty), a Gateway connection string that cannot
    /// be parsed to derive from, and a hosted Gateway with no PostgreSQL database named at all. A hosted
    /// Gateway lands here rather than opening a local statistics file, under any circumstance.</summary>
    NotConfigured,

    /// <summary>A statistics store IS configured, and it could not be reached, opened or migrated. A
    /// database or network problem, and deliberately NOT the same reason as <see cref="NotConfigured"/>:
    /// this one is fixed by fixing the database, and the settings are already right.
    ///
    /// This is the case the containment boundary exists for. The Gateway boots, serves its roster and serves
    /// its tunnels; only the statistics surface is off, and it says why.</summary>
    Unreachable,

    /// <summary>
    /// A FAULT IN DEVTHROTTLE'S OWN CODE, not in the operator's database, network or settings.
    ///
    /// WHY THIS MEMBER EXISTS, AND IT IS THE MECHANISM BEHIND A WHOLE CLASS OF DEFECT. A containment that
    /// catches EVERYTHING cannot tell "the store is unreachable" from "we have a bug" - so without this,
    /// every programming error inside the boundary is handed a plausible INFRASTRUCTURE label and sends the
    /// reader somewhere the fault is not. It is not hypothetical and it is not rare: on 2026-07-30 it
    /// happened three times in one day - an endpoint catch reporting a null reference as a storage fault, a
    /// watcher reading a cancelled run as an answer, and a missing entry in this file's own reason-code map
    /// reported as an unreachable database.
    ///
    /// THE OPERATOR SENTENCE SAYS IT IS OURS, and that is the whole point. A user sent to check their
    /// network for a bug in our switch statement is worse off than a user told "something in DevThrottle's
    /// own code failed", because the second is at least TRUE, and it is actionable by them in the only way
    /// that matters - telling us. The exception type and stack go to the log, never to the surface.
    ///
    /// This reason must NEVER be reported as <see cref="Unreachable"/> or <see cref="NotConfigured"/>. Those
    /// two make a claim about the OPERATOR'S world; this one makes a claim about ours.
    /// </summary>
    InternalError,

    /// <summary>
    /// The store did not answer within the STARTUP deadline, so the Gateway finished starting without it -
    /// and the attempt IS STILL RUNNING and will publish on its own if it succeeds.
    ///
    /// DELIBERATELY NOT <see cref="Unreachable"/>, on the same rule as every other member here: the two lead
    /// to DIFFERENT OPERATOR ACTIONS. Unreachable means go and look - at the database, the network, the
    /// credentials. This one means WAIT AND RE-CHECK, because nothing is known to be wrong yet and the most
    /// likely cause is something slow and local, such as another writer holding a lock. Sending somebody to
    /// audit a network because a lock was held for a few seconds is the same misdirection this set of reasons
    /// exists to prevent, arriving through TIMING rather than through classification.
    ///
    /// It is also the only unavailability here that can clear ITSELF, with no restart and no intervention.
    /// Any surface rendering it should say so, or a reader will treat a transient state as a permanent one.
    /// </summary>
    DidNotAnswerInTime,
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
