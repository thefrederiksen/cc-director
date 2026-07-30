using System.Data.Common;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The statistics store's WRITE path, on Entity Framework, over <see cref="GatewayStatsDbContext"/> - one
/// implementation serving SQLite (self-host) and PostgreSQL (hosted).
///
/// THE ONE RULE THIS CLASS EXISTS TO CARRY:
///
///     NEVER LEARN WHAT YOU CHANGED FROM YOUR OWN PRIOR BELIEF - LEARN IT FROM THE RESPONSE OF WHATEVER
///     ARBITRATES.
///
/// Here the arbiter is the database, and everything below is that single rule applied four times.
///
/// WHY. The store has a shared ledger (the append-only delta tables, which the all-time totals are the SUM of)
/// and a shared watermark (the high-water tables, which say how much of each session has been accounted for).
/// The aggregator also keeps a PRIVATE MIRROR of that watermark in memory so an unchanged roster poll costs
/// nothing. The design as first ported let each writer compute a session's growth against its OWN mirror and
/// append that growth to the SHARED ledger, while the watermark itself was arbitrated by the database. Those
/// two cannot both be authoritative, and every defect this class was reshaped to fix is that one contradiction
/// surfacing somewhere different:
///
///   Two containers poll the same session. The stored watermark is 5. A sees 10 and appends 5. B, whose mirror
///   still says 5 because it has not seen A's write, sees 12 and appends 7. The watermark ends at 12 - correct,
///   because the raise compares. The ledger has gained 12 - wrong, because it should have gained 7. The
///   watermark assertion passes and the totals are quietly inflated, which is exactly the shape of bug that
///   ships: the number that is easy to assert was always going to be right, and the number nobody asserted was
///   always going to be wrong.
///
/// THE FIX IS NOT A GUARD. Detecting a stale baseline and skipping would leave the store's arithmetic resting
/// on a check that has to keep being right. Instead the raise statement RETURNS what the row held before it
/// and what it holds after, in the SAME atomic statement, and the writer appends exactly that difference.
/// Growth becomes WHAT THIS WRITER ADDED TO THE SHARED VALUE rather than what it BELIEVED it was adding, so
/// the sum of appended deltas equals the movement of the watermark BY CONSTRUCTION. The stale-baseline case
/// does not get detected - it stops existing.
///
/// THE SAME RULE, THE OTHER THREE TIMES:
///
///  - RETENTION archives exactly the rows ITS OWN STATEMENT REMOVED. It used to fold rows it had read a moment
///    earlier into archive rows and then delete "the same" predicate again - two statements, two snapshots, so
///    anything that arrived in between was deleted having never been archived, and an all-time total silently
///    shrank ninety days after the write that caused it. Now the DELETE returns the rows it took and those
///    exact rows are what is folded.
///  - IDENTITY MINTING reads back WHICH ID WON. It used to insert a row and take the id the insert generated,
///    which is only the right id if no other writer minted the same spelling at the same moment; when one did,
///    a tenant's turns split silently across two surrogate ids. Now the mint is an upsert against the (tenant,
///    spelling) unique index and the statement reports the surviving id, whoever created it.
///  - THE FIRST-FOLD BACK-FILL attributes a session's standing count to its agent only when THIS writer's
///    <c>agents_seeded</c> insert claimed the row, which the statement reports. Two writers first-folding one
///    session used to both see an unseeded mirror and both attribute it.
///
/// AND ONE RULE THAT CAME WITH THEM, because making writers contend for shared ROWS is what created it:
/// EVERY SET OF ROWS A BATCH LOCKS IS TAKEN IN ONE CANONICAL ORDER (see the sort at the top of
/// <see cref="Commit"/>). A batch arrives in observation order, which two containers polling the same Director
/// have no reason to agree on; opposite orders over the same two rows is a cycle, and PostgreSQL breaks a
/// cycle by ABORTING one transaction with SQLSTATE 40P01 - a lost fold, not a slow one. This was found by
/// review, in production code, after the identity-mint fix introduced it. The order makes the cycle
/// impossible; a retry would only have made it survivable.
///
/// WHAT IS STILL TRUE AND MUST STAY TRUE:
///
///  - EVERY high-water and membership write is an explicit statement, never a change-tracked read-then-save.
///    A read-modify-write on a high-water row is a lost-update generator under concurrent PostgreSQL, and
///    schema version 5 passed that trivially only because the Gateway was a single process holding one
///    connection under one lock. That premise is false on the hosted Gateway, where a slot swap runs two
///    containers against one database at once.
///  - THE RAISE COMPARES; IT NEVER OVERWRITES BLINDLY. Under <c>DO UPDATE SET turns = excluded.turns</c> the
///    row takes whatever the last writer proposed, so the loser of a race pushes the watermark DOWN. The
///    stored row is a floor.
///  - THE RESET RULE IS UNCHANGED IN MEANING and has moved to where it can be applied honestly. A reported
///    count that DROPPED means a Director restarted that session id and is counting fresh from zero, so the
///    whole current count is new activity. Under one writer that was unambiguous. Under two it is not: a
///    count below the stored watermark is EITHER a restart OR a stale read another writer has already
///    overtaken, and they look identical from the outside. So the writer sends the baseline it BELIEVED the
///    store held, as evidence, and the statement decides: baseline current -> a real reset, adopt the reported
///    count and report the whole of it as new activity; baseline already overtaken -> a stale read, keep the
///    floor and report nothing. The belief is an input to the arbiter, never the authority on what changed.
///  - The append-only delta tables and the archive rows are ordinary Entity Framework inserts. An INSERT of a
///    brand new row is not a read-modify-write and has no lost update to lose.
///
/// WHAT THIS DOES NOT COVER, stated rather than left to be discovered, AND CORRECTED AFTER REVIEW BECAUSE THE
/// FIRST STATEMENT OF IT WAS TOO KIND TO ITSELF: two writers observing DIFFERENT INCARNATIONS of one session
/// concurrently - a Director restart landing between two containers' polls, so one writer is still carrying a
/// pre-restart reading while the other has already reset the row. Telling those two readings apart needs an
/// incarnation stamp from the producer, which the wire contract does not carry.
///
/// This was originally written down as "bounded to one poll interval, one session". THE SESSION BOUND IS
/// REAL; THE POLL-INTERVAL BOUND WAS NOT, and reading it as one understated the defect. One poll interval
/// describes at best how long both readings are in the air. It does not bound the ERROR, which lands in the
/// append-only ledger and is therefore PERMANENT - nothing in this store rewrites an appended delta - and
/// whose MAGNITUDE SCALES WITH THE PRE-RESET WATERMARK rather than with any interval. Measured: 100 banked
/// turns, a new-incarnation reset to 5, then a delayed old-incarnation observation of 110 carrying the stale
/// belief of 100. Watermark ended at 110, ledger at 210, honest activity 115 - one collision overcounting by
/// 95, for good. An in-flight writer held up on database work can also land later than one nominal interval.
///
/// So: rare, one session per collision, unbounded in size, and permanent until an incarnation stamp exists or
/// the row is repaired by hand. That is what it is; the earlier phrasing made it sound self-limiting.
///
/// Threading: NOT thread-safe by itself, and it does not need to be - callers serialise (the aggregator holds
/// its own lock, and one batch is one tenant). Two INDEPENDENT writers on two processes against one database
/// are exactly the case the statements above are for, and that is the case the interleaved-writer proof drives.
/// </summary>
internal sealed class GatewayStatsWriter
{
    private readonly IDbContextFactory<GatewayStatsDbContext> _contexts;
    private long _statements;

    // The statements, built once for this writer's provider. A fold happens on every roster poll, so building
    // them per commit would be work and a log line per poll; the provider cannot change under a writer.
    private StatsUpsertSql? _sql;
    private readonly object _sqlLock = new();

    /// <summary>Every write statement this writer has executed. Counts one per row written plus the retention
    /// statements; Entity Framework may batch several inserts into one round trip, so this is an exact count
    /// of WRITES and an upper bound on round trips. The seam acceptance criterion measures it: an IDLE poll
    /// must not move it at all, and a fold must move it by an amount bounded by what CHANGED, never by how
    /// much history is stored.</summary>
    public long StatementsExecuted => Interlocked.Read(ref _statements);

    public GatewayStatsWriter(IDbContextFactory<GatewayStatsDbContext> contexts)
    {
        _contexts = contexts;
    }

    /// <summary>
    /// Write everything <paramref name="batch"/> observed, in ONE transaction, and return WHAT CHANGED as the
    /// database reported it, so the caller can advance its mirror to what the store actually holds.
    ///
    /// An EMPTY batch - an idle poll - writes nothing, creates no context and does not even open a
    /// transaction.
    /// </summary>
    /// <param name="batch">One tenant's collected observation.</param>
    /// <param name="resolveKnownIdentity">Resolves a display spelling this batch did NOT have to mint to the
    /// id the caller's identity mirror already holds for it, within the batch's tenant. It is the caller's map
    /// because the map is what DECIDES identity - a case-insensitive comparer this store deliberately never
    /// asks a database to reproduce - and because a spelling the mirror already knows needs no round trip,
    /// which is what keeps an idle poll free.</param>
    /// <param name="seams">Test seams, and only test seams. The interleaved-writer proof has to CAUSE the
    /// race rather than watch for it, which needs two control points inside one commit: one to hold a
    /// transaction open after its raise, and one that fires as a specific row is about to be raised.
    /// Production passes null and both are no-ops. See <see cref="StatsWriteSeams"/>.</param>
    public StatsCommitResult Commit(
        StatsWriteBatch batch,
        Func<string, IdentityKind, long> resolveKnownIdentity,
        StatsWriteSeams? seams = null)
    {
        var result = new StatsCommitResult();
        if (batch.IsEmpty) return result;

        var tenant = batch.Tenant.Value;
        using var ctx = _contexts.CreateDbContext();
        var sql = StatementsFor(ctx);
        using var tx = ctx.Database.BeginTransaction();

        // ---- Take every row lock in ONE CANONICAL ORDER. ----
        //
        // A DEADLOCK IS A FAILED REQUEST, and the fix for it is an ORDER, not a retry.
        //
        // Every statement below takes a row lock, and the batch arrives in OBSERVATION order - the order the
        // roster happened to list its sessions in, which two hosted containers polling the same Director have
        // no reason to agree on. Writer A minting "owner/one" then "owner/two" while writer B mints them the
        // other way round is a lock cycle, and PostgreSQL resolves a cycle by ABORTING one of the transactions
        // with SQLSTATE 40P01. That is not a slow request, it is a lost fold - and it became possible the
        // moment the identity mint started conflicting on a unique index, because that is what made two
        // writers contend for the SAME identity ROW rather than each inserting its own.
        //
        // Sorting first makes the cycle impossible rather than survivable. A retry would be second best: it
        // would turn a lost fold into a slow one while leaving the cycle in the design, and it would need a
        // budget, a backoff and a test nobody can write deterministically. Two writers that take the same rows
        // in the same order cannot form a cycle at all - there is nothing left to detect.
        //
        // The comparer has to be TOTAL and IDENTICAL IN EVERY PROCESS, so it is StringComparer.Ordinal
        // throughout: byte order, no culture, no case folding. It deliberately does NOT match the
        // case-insensitive comparer that decides identity - it does not have to. All that is required is that
        // any two writers order THE SAME ROW the same way, and the same row means the same key bytes.
        //
        // The order BETWEEN tables is already canonical: it is the order of the code below, which is the same
        // code in both processes. Only the order WITHIN each set was free, and this is where it stops being.
        var identities = batch.NewIdentities
            .OrderBy(i => (int)i.Kind).ThenBy(i => i.Display, StringComparer.Ordinal).ToList();
        var buckets = batch.Buckets
            .OrderBy(b => b.SessionId, StringComparer.Ordinal)
            .ThenBy(b => b.Modality, StringComparer.Ordinal)
            .ThenBy(b => b.Surface, StringComparer.Ordinal).ToList();
        var agentDriven = batch.AgentDriven.OrderBy(a => a.SessionId, StringComparer.Ordinal).ToList();
        var tokens = batch.Tokens.OrderBy(t => t.SessionId, StringComparer.Ordinal).ToList();
        var seeding = batch.Seeding.OrderBy(s => s.SessionId, StringComparer.Ordinal).ToList();
        var wingman = batch.NewWingmanSessions.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var identitySessions = batch.NewIdentitySessions
            .OrderBy(m => (int)m.Kind)
            .ThenBy(m => m.Display, StringComparer.Ordinal)
            .ThenBy(m => m.SessionId, StringComparer.Ordinal).ToList();

        // agents_since is per tenant (MTR-08): (tenant, name) is the key, and the stamp is written ONCE and
        // never moved. Insert-if-absent, so a tenant that already has a start keeps its own earliest one even
        // when a second writer's mirror had not seen it yet. One row, so it has no order to get wrong, and it
        // goes first in both processes.
        if (batch.StampAgentsSince is not null)
            Execute(ctx, sql.InsertMetaIfAbsent, tenant, GatewayStatsAggregatorKeys.AgentsSince, batch.StampAgentsSince);

        // ---- Identity, first: every row below is filed under a surrogate id. ----

        var resolvedIds = ResolveIdentities(ctx, sql, tenant, identities);
        foreach (var (kind, map) in resolvedIds) result.Identities[kind] = map;

        long Resolve(string display, IdentityKind kind) =>
            resolvedIds[kind].TryGetValue(display, out var id) ? id : resolveKnownIdentity(display, kind);

        // An absent model resolves to nothing at all - SQL NULL, rather than a sentinel id a later reader
        // could mistake for a real model.
        long? ResolveModel(string? display) => display is null ? null : Resolve(display, IdentityKind.Model);

        // ---- The three high-water paths. Raise, learn what the raise changed, append exactly that. ----
        //
        // The order is not decorative: the delta row CANNOT be built before the raise, because the raise is
        // what says how big it is. Anything that computed the row first would be back to appending a believed
        // difference to a shared ledger, which is the defect this shape exists to remove.

        var deltaRows = 0;

        foreach (var b in buckets)
        {
            var raised = RaiseSessionHighWater(ctx, sql, tenant, b, seams);
            var turns = raised.Metrics[0];
            var chars = raised.Metrics[1];
            result.SessionHighWater.Add((b.SessionId, b.Modality, b.Surface, turns.Stored, chars.Stored, raised.Generation));
            if (turns.Growth <= 0 && chars.Growth <= 0) continue;

            ctx.StatDeltas.Add(new StatDeltaEntity
            {
                Tenant = tenant,
                HourUtc = batch.HourKey,
                SessionId = b.SessionId,
                Modality = b.Modality,
                Surface = b.Surface,
                IsVoice = b.IsVoice,
                RepoId = Resolve(b.Repo, IdentityKind.Repo),
                CheckoutId = Resolve(b.Checkout, IdentityKind.Checkout),
                ModelId = ResolveModel(b.Model),
                Wingman = b.Wingman,
                Turns = turns.Growth,
                Chars = chars.Growth,
            });
            deltaRows++;

            // The per-agent tally is attributed from the SAME difference, never recomputed. Deriving it a
            // second time is how two numbers that must agree stop agreeing.
            ctx.AgentDeltas.Add(new AgentDeltaEntity
            {
                Tenant = tenant,
                AgentId = Resolve(b.Agent, IdentityKind.Agent),
                IsVoice = b.IsVoice,
                Turns = turns.Growth,
                Chars = chars.Growth,
            });
            deltaRows++;
        }

        foreach (var a in agentDriven)
        {
            var driven = RaiseAgentDrivenHighWater(ctx, sql, tenant, a, seams);
            var turns = driven.Metrics[0];
            var chars = driven.Metrics[1];
            result.AgentDrivenHighWater.Add((a.SessionId, turns.Stored, chars.Stored, driven.Generation));
            if (turns.Growth <= 0 && chars.Growth <= 0) continue;

            ctx.AgentDrivenDeltas.Add(new AgentDrivenDeltaEntity
            {
                Tenant = tenant,
                AgentId = Resolve(a.Agent, IdentityKind.Agent),
                Turns = turns.Growth,
                Chars = chars.Growth,
            });
            deltaRows++;
        }

        foreach (var t in tokens)
        {
            var spend = RaiseTokenHighWater(ctx, sql, tenant, t, seams);
            var m = spend.Metrics;
            result.TokenHighWater.Add((t.SessionId,
                m[0].Stored, m[1].Stored, m[2].Stored, m[3].Stored, spend.Generation));
            if (m.All(r => r.Growth <= 0)) continue;

            ctx.TokenDeltas.Add(new TokenDeltaEntity
            {
                Tenant = tenant,
                HourUtc = batch.HourKey,
                ModelId = ResolveModel(t.Model),
                InputTokens = m[0].Growth,
                OutputTokens = m[1].Growth,
                CacheReadTokens = m[2].Growth,
                CacheCreationTokens = m[3].Growth,
            });
            deltaRows++;
        }

        // ---- The first-fold back-fill (issue #1633), attributed only by the writer that CLAIMED the mark. ----
        //
        // agents_seeded is insert-if-absent, and the statement reports whether THIS insert is the one that
        // created the row. Two writers first-folding one session both find an unseeded mirror; without this,
        // both would attribute the session's standing count and the agent's numbers would double.

        foreach (var (sessionId, rows) in seeding)
        {
            if (!ClaimedSeeding(ctx, sql, tenant, sessionId)) continue;
            foreach (var row in rows)
            {
                ctx.AgentDeltas.Add(new AgentDeltaEntity
                {
                    Tenant = tenant,
                    AgentId = Resolve(row.Agent, IdentityKind.Agent),
                    IsVoice = row.IsVoice,
                    Turns = row.Turns,
                    Chars = row.Chars,
                });
                deltaRows++;
            }
        }

        if (deltaRows > 0)
        {
            ctx.SaveChanges();
            Interlocked.Add(ref _statements, deltaRows);
        }

        // ---- The membership sets. Insert-if-absent, never a read-then-insert. ----

        foreach (var sid in wingman)
            Execute(ctx, sql.InsertWingmanSessionIfAbsent, tenant, sid);

        foreach (var (display, sessionId, kind) in identitySessions)
        {
            // A model and a checkout keep no distinct-session set, deliberately. One queued here is a
            // programming error and says so, loudly, rather than writing a row into a table that does not
            // exist. Nothing queues one today, and this is the check that keeps that true.
            var statement = kind switch
            {
                IdentityKind.Repo => sql.InsertRepoSessionIfAbsent,
                IdentityKind.Agent => sql.InsertAgentSessionIfAbsent,
                _ => throw new ArgumentOutOfRangeException(nameof(batch), kind,
                    "Only repositories and agents keep distinct-session sets."),
            };
            Execute(ctx, statement, Resolve(display, kind), sessionId);
        }

        if (batch.Buckets.Count > 0 || batch.Tokens.Count > 0)
            Prune(ctx, sql, tenant, batch.NowUtc);

        seams?.BeforeCommit?.Invoke();
        tx.Commit();
        return result;
    }

    /// <summary>
    /// Drop one session's high-water rows for one tenant. Its contribution stays in the totals - it was
    /// folded in as it happened; dropping the watermark just stops the table growing without bound.
    /// </summary>
    /// <returns>Whether a row was deleted, so the caller can keep its mirror and the store in step.</returns>
    public bool DeleteSessionHighWater(string tenant, string sessionId)
    {
        using var ctx = _contexts.CreateDbContext();
        return Execute(ctx, StatementsFor(ctx).DeleteSessionHighWater, tenant, sessionId) > 0;
    }

    /// <summary>Drop one session's token high-water row for one tenant. Cleaned up on its own, not under the
    /// session_highwater guard: a session has both, and dropping only one would leave the other's map growing
    /// without bound.</summary>
    public bool DeleteTokenHighWater(string tenant, string sessionId)
    {
        using var ctx = _contexts.CreateDbContext();
        return Execute(ctx, StatementsFor(ctx).DeleteTokenHighWater, tenant, sessionId) > 0;
    }

    // ---- Raising a watermark, and learning from the database what the raise changed -----------------------

    /// <summary>One raised metric: what the row holds now, and how much THIS statement added to it.</summary>
    private readonly record struct Raised(long Stored, long Growth);

    // Turn a raise statement's two returned halves into a growth. THE SAME RULE the fold used to apply to its
    // own mirror, applied instead to the database's answer - which is the whole change:
    //
    //   stored >= previous : the row moved forward (or not at all). The growth is the difference.
    //   stored <  previous : the statement LOWERED the row, which it does in exactly one case - an observed
    //                        reset, where the session began counting again from zero. The whole of the new
    //                        count is new activity.
    //
    // A stale read that another writer has overtaken leaves stored == previous, so it contributes zero. It
    // needs no test of its own here; the statement already made that ruling.
    private static Raised Grow(long stored, long previous) =>
        new(stored, stored >= previous ? stored - previous : stored);

    /// <summary>What one raise reported: the growth per metric, and the incarnation the row is now on.</summary>
    private readonly record struct RaisedRow(Raised[] Metrics, long Generation);

    private RaisedRow RaiseSessionHighWater(
        GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant,
        StatsWriteBatch.BucketObservation b, StatsWriteSeams? seams)
    {
        seams?.BeforeRaise?.Invoke($"session_highwater:{b.SessionId}/{b.Modality}/{b.Surface}");
        return Raised2(ReadRow(ctx, sql.RaiseSessionHighWater,
            tenant, b.SessionId, b.Modality, b.Surface,
            b.ReportedTurns, b.ReportedChars,
            b.BelievedTurns, b.BelievedChars, b.BelievedGeneration));
    }

    private RaisedRow RaiseAgentDrivenHighWater(
        GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant,
        StatsWriteBatch.AgentDrivenObservation a, StatsWriteSeams? seams)
    {
        seams?.BeforeRaise?.Invoke($"agent_driven_highwater:{a.SessionId}");
        return Raised2(ReadRow(ctx, sql.RaiseAgentDrivenHighWater,
            tenant, a.SessionId,
            a.ReportedTurns, a.ReportedChars,
            a.BelievedTurns, a.BelievedChars, a.BelievedGeneration));
    }

    private RaisedRow RaiseTokenHighWater(
        GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant,
        StatsWriteBatch.TokenObservation t, StatsWriteSeams? seams)
    {
        seams?.BeforeRaise?.Invoke($"token_highwater:{t.SessionId}");
        var r = ReadRow(ctx, sql.RaiseTokenHighWater,
            tenant, t.SessionId,
            t.ReportedInput, t.ReportedOutput, t.ReportedCacheRead, t.ReportedCacheCreation,
            t.BelievedInput, t.BelievedOutput, t.BelievedCacheRead, t.BelievedCacheCreation,
            t.BelievedGeneration);
        return new RaisedRow(
            new[]
            {
                Grow(Number(r[0]), Number(r[1])), Grow(Number(r[2]), Number(r[3])),
                Grow(Number(r[4]), Number(r[5])), Grow(Number(r[6]), Number(r[7])),
            },
            Number(r[8]));
    }

    // The two-metric shape, which session_highwater and agent_driven_highwater share: value, previous, value,
    // previous, generation.
    private static RaisedRow Raised2(object[] r) =>
        new(new[] { Grow(Number(r[0]), Number(r[1])), Grow(Number(r[2]), Number(r[3])) }, Number(r[4]));

    // Mark a session back-filled and report whether THIS statement is the one that marked it. Insert-if-absent
    // with a RETURNING clause: a conflict does nothing and returns no row, so an empty result means another
    // writer got there first - which is a fact about the world, not a failure.
    private bool ClaimedSeeding(GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant, string sessionId)
    {
        using var cmd = Command(ctx, sql.ClaimAgentsSeeded, tenant, sessionId);
        using var reader = cmd.ExecuteReader();
        Interlocked.Add(ref _statements, 1);
        return reader.Read();
    }

    // ---- Identity: mint or find, and take the id the database says WON ------------------------------------

    // Resolve every display spelling this batch could not answer from the caller's mirror. Each is an upsert
    // against the (tenant, spelling) unique index that RETURNS the surviving surrogate id - which is this
    // writer's id when it wins the race and the other writer's when it does not. Taking the id from a plain
    // insert instead would be assuming the mint succeeded, and when two hosted containers mint the same
    // spelling at the same moment one of them is wrong: either the insert fails outright (unique violation
    // taking the whole batch down) or, without the index, two ids exist for one identity and that tenant's
    // turns split silently across both.
    //
    // The DATABASE STILL DOES NOT DECIDE IDENTITY. The conflict target is the exact byte-for-byte spelling; two
    // spellings differing only by case are two rows here, and it is the caller's OrdinalIgnoreCase mirror -
    // which is why this method is only ever asked about spellings that mirror did not recognise - that folds
    // them into one. See RepoIdentityEntity.
    /// <param name="identities">Already in canonical order - see the sort at the top of
    /// <see cref="Commit"/>. This method must not re-order them: minting is where the deadlock lived, because
    /// the unique index is what made two writers contend for the same identity ROW.</param>
    private Dictionary<IdentityKind, Dictionary<string, long>> ResolveIdentities(
        GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant,
        IReadOnlyList<(string Display, IdentityKind Kind)> identities)
    {
        // Keyed with the SAME comparer as the mirror they will join, so two spellings differing only by case
        // resolve to one identity here exactly as they do there.
        var resolved = new Dictionary<IdentityKind, Dictionary<string, long>>
        {
            [IdentityKind.Repo] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Agent] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Model] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Checkout] = new(StringComparer.OrdinalIgnoreCase),
        };
        if (identities.Count == 0) return resolved;

        foreach (var (display, kind) in identities)
        {
            var statement = kind switch
            {
                IdentityKind.Repo => sql.MintRepoIdentity,
                IdentityKind.Agent => sql.MintAgentIdentity,
                IdentityKind.Model => sql.MintModelIdentity,
                IdentityKind.Checkout => sql.MintCheckoutIdentity,
                _ => throw new ArgumentOutOfRangeException(nameof(identities), kind, "Unknown identity kind."),
            };
            resolved[kind][display] = Number(ReadRow(ctx, statement, tenant, display)[0]);
        }

        return resolved;
    }

    // ---- Retention ---------------------------------------------------------------------------------------

    // Prune the working-day detail past the retention window, for ONE tenant only, inside the caller's
    // transaction.
    //
    // THE ROWS ARCHIVED ARE THE ROWS THIS STATEMENT REMOVED, not rows read a moment earlier and deleted by a
    // predicate a moment later. The two-statement shape (fold into an archive row, then delete by the same
    // WHERE clause) reads the table twice: anything committed between the two reads is deleted having never
    // been folded, so an all-time total silently shrinks - and it shrinks ninety days after the write that
    // caused it, which is long past any chance of connecting the two. The DELETE now returns what it took and
    // that is what is folded; nothing else can be in the set, whatever else commits alongside.
    //
    // PER-PARTITION (MTR-08, the same class as the car-mode #1933 global-prune bug): the delete is constrained
    // to this tenant - without that, a caller's write would archive and delete EVERY tenant's rows older than
    // the cutoff, a cross-tenant mutation that violates the invariant that a caller's write only ever touches
    // its own partition. A quiet tenant's expired detail is reclaimed when THAT tenant next writes; a global
    // age-sweep, if ever wanted, is a background job, never a side effect of an unrelated tenant's fold.
    //
    // Departing rows are folded into ARCHIVE rows rather than dropped. One row here feeds both the hourly
    // series and the all-time totals, so deleting it outright would silently shrink the all-time totals - the
    // #1376 class of failure. The fold preserves every dimension any all-time answer groups by; pruning
    // collapses the hour and the session id, and nothing else. Adding a dimension to stat_delta means adding
    // it to the grouping key below, or pruning quietly destroys it. agent_delta and agent_driven_delta carry
    // no hour and are never pruned, matching the all-time agent tally.
    //
    // The fold is done here rather than in SQL because the rows are already in hand: they came back from the
    // DELETE, and re-reading them from a table they are no longer in is not possible. Grouping them in one
    // place, with one key, also removes the version of this hazard the SQL shape had - a dimension present in
    // the SELECT list but missing from the GROUP BY, which collapses different values into one row and takes
    // an arbitrary id with it. Here the key IS the projection.
    private void Prune(GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant, DateTime nowUtc)
    {
        var cutoff = StatsHourKey.For(nowUtc.AddDays(-StatsHourKey.RetentionDays));
        var marker = GatewayStatsDatabase.ArchiveMarker;
        var archived = 0;

        // stat_delta. Null model and null checkout ids group together as null - absence aggregates as
        // absence, and the archive row is still honestly "not recorded" rather than attributed to anything.
        var stat = new Dictionary<(string Modality, string Surface, bool IsVoice, long RepoId, long? CheckoutId, long? ModelId, bool Wingman), (long Turns, long Chars)>();
        foreach (var r in ReadRows(ctx, sql.DeleteExpiredStatDelta, tenant, marker, cutoff))
        {
            var key = (Text(r[0]), Text(r[1]), Flag(r[2]), Number(r[3]), NullableNumber(r[4]), NullableNumber(r[5]), Flag(r[6]));
            stat.TryGetValue(key, out var sum);
            stat[key] = (sum.Turns + Number(r[7]), sum.Chars + Number(r[8]));
        }
        foreach (var (key, sum) in stat)
        {
            ctx.StatDeltas.Add(new StatDeltaEntity
            {
                Tenant = tenant,
                HourUtc = marker,
                SessionId = marker,
                Modality = key.Modality,
                Surface = key.Surface,
                IsVoice = key.IsVoice,
                RepoId = key.RepoId,
                CheckoutId = key.CheckoutId,
                ModelId = key.ModelId,
                Wingman = key.Wingman,
                Turns = sum.Turns,
                Chars = sum.Chars,
            });
            archived++;
        }

        // token_delta. Grouped by model alone - it carries no other dimension. The key is wrapped in a struct
        // because the model id is genuinely nullable ("the Director had named no model") and a dictionary key
        // may not be a bare nullable value type; the null grouping is the point, not an edge case.
        var tokens = new Dictionary<ArchiveTokenKey, (long Input, long Output, long CacheRead, long CacheCreation)>();
        foreach (var r in ReadRows(ctx, sql.DeleteExpiredTokenDelta, tenant, marker, cutoff))
        {
            var key = new ArchiveTokenKey(NullableNumber(r[0]));
            tokens.TryGetValue(key, out var sum);
            tokens[key] = (sum.Input + Number(r[1]), sum.Output + Number(r[2]),
                           sum.CacheRead + Number(r[3]), sum.CacheCreation + Number(r[4]));
        }
        foreach (var (model, sum) in tokens)
        {
            ctx.TokenDeltas.Add(new TokenDeltaEntity
            {
                Tenant = tenant,
                HourUtc = marker,
                ModelId = model.ModelId,
                InputTokens = sum.Input,
                OutputTokens = sum.Output,
                CacheReadTokens = sum.CacheRead,
                CacheCreationTokens = sum.CacheCreation,
            });
            archived++;
        }

        if (archived > 0)
        {
            ctx.SaveChanges();
            Interlocked.Add(ref _statements, archived);
        }
    }

    /// <summary>The archive grouping key for token_delta - the model, which may honestly be absent.</summary>
    private readonly record struct ArchiveTokenKey(long? ModelId);

    private static string Text(object value) => (string)value;
    private static bool Flag(object value) => value is bool b ? b : Convert.ToInt64(value) != 0;
    private static long Number(object value) => Convert.ToInt64(value);
    private static long? NullableNumber(object value) => value is DBNull ? null : Convert.ToInt64(value);

    // ---- Statement plumbing ------------------------------------------------------------------------------

    private StatsUpsertSql StatementsFor(GatewayStatsDbContext ctx)
    {
        lock (_sqlLock)
        {
            if (_sql is null)
            {
                _sql = new StatsUpsertSql(ctx);
                FileLog.Write($"[GatewayStatsWriter] StatementsFor: built the write statements for " +
                              $"provider={ctx.Database.ProviderName}");
            }
            return _sql;
        }
    }

    // Every raw statement is built here, with real provider parameters named @p0, @p1, ... - a spelling both
    // Npgsql and Microsoft.Data.Sqlite accept, so one statement text serves both providers and no value is
    // ever concatenated into SQL. The command joins whatever transaction the context is already in, which is
    // what puts every statement of a batch under the one commit.
    private static DbCommand Command(GatewayStatsDbContext ctx, string statement, params object?[] args)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) ctx.Database.OpenConnection();

        var cmd = connection.CreateCommand();
        cmd.CommandText = statement;
        cmd.Transaction = ctx.Database.CurrentTransaction?.GetDbTransaction();
        for (var i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private int Execute(GatewayStatsDbContext ctx, string statement, params object?[] args)
    {
        using var cmd = Command(ctx, statement, args);
        var affected = cmd.ExecuteNonQuery();
        Interlocked.Add(ref _statements, 1);
        return affected;
    }

    // Run a statement that MUST return exactly one row, and hand back its columns. Every caller is a raise or
    // a mint: each writes precisely one row and each is worthless without the answer, so no row back is a
    // broken statement rather than an empty result to be tolerated.
    private object[] ReadRow(GatewayStatsDbContext ctx, string statement, params object?[] args)
    {
        var rows = ReadRows(ctx, statement, args);
        if (rows.Count != 1)
            throw new InvalidOperationException(
                $"A statistics write statement returned {rows.Count} rows where exactly one was required. " +
                $"The statement was: {statement}");
        return rows[0];
    }

    // Run a statement with a RETURNING clause and materialise every row BEFORE anything else touches the
    // table. SQLite may emit RETURNING rows while the statement is still running, and modifying the same table
    // mid-read is undefined there - so the reader is drained and closed first, always, rather than only where
    // it currently happens to matter.
    private List<object[]> ReadRows(GatewayStatsDbContext ctx, string statement, params object?[] args)
    {
        var rows = new List<object[]>();
        using (var cmd = Command(ctx, statement, args))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
        }
        Interlocked.Add(ref _statements, 1);
        return rows;
    }
}

/// <summary>The meta keys this store's aggregator owns. <c>models_since_utc</c> is not here: it is a schema
/// fact stamped by a migration, not something the write path writes.</summary>
internal static class GatewayStatsAggregatorKeys
{
    public const string AgentsSince = "agents_since_utc";
}

/// <summary>The hour key format and the retention window the write path prunes to.</summary>
internal static class StatsHourKey
{
    public const string Format = "yyyy-MM-ddTHH";
    public const int RetentionDays = 90;

    public static string For(DateTime utc) =>
        utc.ToUniversalTime().ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The write path's statements, built once per context for the context's own provider.
///
/// Shared where the dialects agree - and they agree on nearly all of it. Both SQLite and PostgreSQL spell the
/// upsert <c>INSERT ... ON CONFLICT (key) DO UPDATE SET</c> / <c>DO NOTHING</c>, both expose the proposed row
/// as <c>excluded</c>, and both support <c>RETURNING</c> on an INSERT, an UPSERT and a DELETE - which is what
/// lets the rule this whole write path rests on (learn what you changed from the arbiter, never from your own
/// belief) be ONE implementation rather than two. Provider-specific in exactly two places, both named here so
/// nobody has to go looking for a third:
///
///  1. NAMING THE EXISTING ROW inside a DO UPDATE. PostgreSQL wants it qualified by the table name;
///     SQLite wants it bare.
///  2. THE TABLE NAME. On PostgreSQL every table lives in the <c>gateway_stats</c> schema; SQLite is
///     schemaless.
/// </summary>
internal sealed class StatsUpsertSql
{
    private readonly bool _postgres;
    private readonly string _prefix;

    public StatsUpsertSql(GatewayStatsDbContext ctx)
    {
        _postgres = ctx.Database.IsNpgsql();
        _prefix = _postgres ? GatewayStatsDbContext.PostgresSchema + "." : "";
    }

    private string Table(string name) => _prefix + name;

    // How the EXISTING row's column is named inside a DO UPDATE clause. The one dialect difference that runs
    // through every raise statement below. Note the table name is NOT schema-qualified here even on
    // PostgreSQL: inside ON CONFLICT DO UPDATE the target is named by its bare table name (or an alias).
    private string Existing(string table, string column) => _postgres ? $"{table}.{column}" : column;

    /// <summary>
    /// A raise: move a watermark row forward, remember what it held before, and return both halves so the
    /// caller can append exactly the difference this statement made.
    ///
    /// The parameters are, in order: the key columns, then the REPORTED cumulative value of each metric, then
    /// the value this writer BELIEVED the row held for each metric, and finally the GENERATION it believed the
    /// row was on.
    ///
    /// The rule each metric follows, in full:
    ///
    ///   belief from an older generation an OLD INCARNATION'S reading, arriving after this row has already
    ///                                   been reset into a new life. Nothing it says is about the life being
    ///                                   counted now. The row does not move and nothing is appended - see the
    ///                                   generation note below, which is the whole reason this branch exists.
    ///   reported >= stored              the ordinary case, a count that has grown. Take the reported value.
    ///   reported <  stored, belief current   a RESET: this writer's baseline was level with or ahead of the
    ///                                   stored row, so it is looking at fresh state and the drop is real - a
    ///                                   Director restarted this session id and is counting from zero again.
    ///                                   Take the reported value; the caller reports the whole of it as new
    ///                                   activity, because that is what the returned previous value says. The
    ///                                   row's GENERATION advances, which is what makes the first branch able
    ///                                   to recognise the readings that belong to the life just ended.
    ///   reported <  stored, belief behind    a STALE read. Another writer has already carried this row past
    ///                                   what this one believed, so its lower number is only lower relative to
    ///                                   a state it never saw. Keep the floor; nothing was added.
    ///
    /// THE GENERATION COLUMN IS WHAT CLOSES THE CROSS-INCARNATION OVERCOUNT, and it needs no change to the
    /// wire contract - the store can see a reset happen, so it can count them. Without it, a delayed reading
    /// from the life BEFORE a reset is indistinguishable from ordinary growth in the life after it: 100 banked
    /// turns, a reset to 5, then a straggling observation of 110 was read as growth of 105 and the ledger
    /// ended at 210 against honest activity of 115. That error is PERMANENT (nothing rewrites an appended
    /// delta) and its size scales with the pre-reset watermark, so it is not a rounding artefact - it is the
    /// statistics being wrong forever for anyone whose Director restarts after a counter reset, which is an
    /// ordinary event. With generations the straggler carries a belief from generation 0 while the row is on
    /// generation 1, so it contributes nothing; the writer's mirror then learns the new row and its next poll
    /// measures growth from the new life's baseline. The error does not get corrected afterwards - it never
    /// happens.
    ///
    /// The last branch is the lost-update protection, and it is why the raise compares rather than overwrites:
    /// under <c>DO UPDATE SET turns = excluded.turns</c> the loser of a race pushes the watermark DOWN and the
    /// same turns are counted again on the next fold.
    ///
    /// Each metric is judged on its own, exactly as the fold judged them: all of a session's counts are
    /// running sums over one transcript, so on a real restart they drop together, and testing each
    /// independently is simply the same safe rule applied per column.
    /// </summary>
    private string Raise(string table, string[] keys, string[] metrics)
    {
        var previous = metrics.Select(m => "previous_" + m).ToArray();
        var parameter = 0;
        var keyValues = keys.Select(_ => "@p" + parameter++).ToArray();
        var reported = metrics.Select(_ => "@p" + parameter++).ToArray();
        var believed = metrics.Select(_ => "@p" + parameter++).ToArray();
        var believedGeneration = "@p" + parameter;

        var stored = metrics.Select(m => Existing(table, m)).ToArray();
        var storedGeneration = Existing(table, "generation");

        // The reading belongs to an incarnation the row has already left behind. Nothing it says is about the
        // life this row is currently counting, so the row does not move and nothing is appended.
        var fromAnOlderLife = $"{believedGeneration} < {storedGeneration}";

        // A reset is being adopted: some metric came back LOWER than the row holds, from a writer whose
        // baseline was level with or ahead of it - so it is looking at current state and the drop is real.
        var adoptingAReset = string.Join(" OR ", metrics.Select((m, i) =>
            $"(excluded.{m} < {stored[i]} AND {believed[i]} >= {stored[i]})"));

        var set = new List<string>();
        // Written before the metric assignments purely for readability: every right-hand side in an UPDATE is
        // evaluated against the ORIGINAL row on both providers, so the order of the SET list cannot matter.
        for (var i = 0; i < metrics.Length; i++)
            set.Add($"{previous[i]} = {stored[i]}");
        for (var i = 0; i < metrics.Length; i++)
            set.Add($@"{metrics[i]} = CASE WHEN {fromAnOlderLife} THEN {stored[i]}
                                          WHEN excluded.{metrics[i]} >= {stored[i]}
                                            OR {believed[i]} >= {stored[i]}
                                          THEN excluded.{metrics[i]}
                                          ELSE {stored[i]} END");
        set.Add($@"generation = CASE WHEN {fromAnOlderLife} THEN {storedGeneration}
                                    WHEN {adoptingAReset} THEN {storedGeneration} + 1
                                    ELSE {storedGeneration} END");

        // The previous_* columns are seeded to zero on a first insert, so a brand new row reports its whole
        // reported count as the difference - which is exactly right: everything about it is new. Generation
        // starts at zero, which is the first life this row has counted.
        return $@"INSERT INTO {Table(table)} ({string.Join(", ", keys)}, {string.Join(", ", metrics)}, {string.Join(", ", previous)}, generation)
                  VALUES ({string.Join(", ", keyValues)}, {string.Join(", ", reported)}, {string.Join(", ", metrics.Select(_ => "0"))}, 0)
                  ON CONFLICT ({string.Join(", ", keys)}) DO UPDATE SET {string.Join(", ", set)}
                  RETURNING {string.Join(", ", metrics.Select((m, i) => $"{m}, {previous[i]}"))}, generation";
    }

    public string RaiseSessionHighWater =>
        Raise("session_highwater", new[] { "tenant", "session_id", "modality", "surface" }, new[] { "turns", "chars" });

    public string RaiseAgentDrivenHighWater =>
        Raise("agent_driven_highwater", new[] { "tenant", "session_id" }, new[] { "turns", "chars" });

    public string RaiseTokenHighWater =>
        Raise("token_highwater", new[] { "tenant", "session_id" },
            new[] { "input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens" });

    // Mint or find one identity and return the id that WON. DO UPDATE rather than DO NOTHING, with an
    // assignment that changes nothing: DO NOTHING returns no row on a conflict, and "no row" would leave the
    // caller with nothing to file its turns under. This way the statement always answers, whether it created
    // the row or found somebody else's.
    private string MintIdentity(string table, string idColumn, string displayColumn) =>
        $@"INSERT INTO {Table(table)} (tenant, {displayColumn}) VALUES (@p0, @p1)
           ON CONFLICT (tenant, {displayColumn}) DO UPDATE SET {displayColumn} = excluded.{displayColumn}
           RETURNING {idColumn}";

    public string MintRepoIdentity => MintIdentity("repo_identity", "repo_id", "repo_display");
    public string MintAgentIdentity => MintIdentity("agent_identity", "agent_id", "agent_display");
    public string MintModelIdentity => MintIdentity("model_identity", "model_id", "model_display");
    public string MintCheckoutIdentity => MintIdentity("checkout_identity", "checkout_id", "checkout_display");

    // Mark a session back-filled and say whether THIS statement did the marking. DO NOTHING returns no row on
    // a conflict, and here that silence is the answer: somebody else claimed it, so this writer attributes
    // nothing. (The opposite choice from the identity mint above, for the opposite reason.)
    public string ClaimAgentsSeeded =>
        $@"INSERT INTO {Table("agents_seeded")} (tenant, session_id) VALUES (@p0, @p1)
           ON CONFLICT (tenant, session_id) DO NOTHING
           RETURNING session_id";

    public string InsertWingmanSessionIfAbsent =>
        $@"INSERT INTO {Table("wingman_session")} (tenant, session_id) VALUES (@p0, @p1)
           ON CONFLICT (tenant, session_id) DO NOTHING";

    // repo_session and agent_session carry no tenant column at schema version 5. They are partitioned
    // INDIRECTLY: repo_id and agent_id are surrogates minted per tenant, so the pair is already
    // tenant-unique. Carried forward unchanged - adding a tenant column here is a behaviour change.
    public string InsertRepoSessionIfAbsent =>
        $@"INSERT INTO {Table("repo_session")} (repo_id, session_id) VALUES (@p0, @p1)
           ON CONFLICT (repo_id, session_id) DO NOTHING";

    public string InsertAgentSessionIfAbsent =>
        $@"INSERT INTO {Table("agent_session")} (agent_id, session_id) VALUES (@p0, @p1)
           ON CONFLICT (agent_id, session_id) DO NOTHING";

    // The since-stamps are written once and never moved, so insert-if-absent is the whole semantic. A second
    // writer whose mirror had not yet seen the stamp proposes its own and the earlier one stands.
    public string InsertMetaIfAbsent =>
        $@"INSERT INTO {Table("meta")} (tenant, name, value) VALUES (@p0, @p1, @p2)
           ON CONFLICT (tenant, name) DO NOTHING";

    public string DeleteSessionHighWater =>
        $"DELETE FROM {Table("session_highwater")} WHERE tenant = @p0 AND session_id = @p1";

    public string DeleteTokenHighWater =>
        $"DELETE FROM {Table("token_highwater")} WHERE tenant = @p0 AND session_id = @p1";

    // Retention, one statement per table: remove this tenant's expired detail and hand back exactly what was
    // removed, so the archive rows are folded from the rows this statement took rather than from a separate
    // read of the same predicate. Archive rows are excluded from the sweep by their marker hour, so they are
    // never re-archived and never re-deleted.
    //
    // Every dimension the archive row must carry is in the RETURNING list. One left out would read NULL on
    // the archive row and every pruned turn would silently lose that dimension - ninety days after the write
    // that caused it.
    public string DeleteExpiredStatDelta =>
        $@"DELETE FROM {Table("stat_delta")}
            WHERE tenant = @p0 AND hour_utc <> @p1 AND hour_utc < @p2
           RETURNING modality, surface, is_voice, repo_id, checkout_id, model_id, wingman, turns, chars";

    public string DeleteExpiredTokenDelta =>
        $@"DELETE FROM {Table("token_delta")}
            WHERE tenant = @p0 AND hour_utc <> @p1 AND hour_utc < @p2
           RETURNING model_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens";
}
