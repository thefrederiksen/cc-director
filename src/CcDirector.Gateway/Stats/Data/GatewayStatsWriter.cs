using CcDirector.Core.Utilities;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The statistics store's WRITE path, on Entity Framework, over <see cref="GatewayStatsDbContext"/> - one
/// implementation serving SQLite (self-host) and PostgreSQL (hosted).
///
/// THE RULING THIS CLASS EXISTS TO CARRY: EVERY HIGH-WATER AND MEMBERSHIP WRITE IS AN EXPLICIT UPSERT.
///
/// The three high-water tables get <c>ON CONFLICT ... DO UPDATE</c>; the four membership sets and the meta
/// since-stamps get <c>ON CONFLICT ... DO NOTHING</c> (which is what SQLite schema version 5's
/// <c>INSERT OR IGNORE</c> meant). Not one of them is a change-tracked read-then-save, and porting one to
/// change tracking would be a silent regression rather than a tidy-up. The reason is the whole reason this
/// port exists:
///
/// A read-modify-write on a high-water row is a LOST-UPDATE GENERATOR under concurrent PostgreSQL. Two
/// writers read the same stored count, each computes its own new value, and the second write lands on a row
/// whose state it never saw - so the higher advance is thrown away, the watermark goes BACKWARDS, and the
/// next fold re-folds turns that were already counted. Schema version 5 passed that trivially because the
/// Gateway was a single process holding one connection under one lock; its own file header says so. That
/// premise is FALSE on the hosted Gateway, where a slot swap runs two containers against one database at the
/// same time. Re-importing the read-then-save shape through an object-relational mapper would carry the exact
/// defect across into the new store while looking modern.
///
/// WHY THE UPDATE IS A GREATEST-STYLE COMPARISON AND NOT AN UNCONDITIONAL OVERWRITE. Under
/// <c>DO UPDATE SET turns = excluded.turns</c> the row takes whatever the last writer proposed, so the loser
/// of a race can still push the watermark DOWN - the same lost update, one layer lower. Comparing means the
/// stored row is a floor that never regresses, and a concurrent writer's advance cannot be lost whatever
/// order the two transactions commit in. The dialects disagree on the spelling, so this is one of the two
/// places the statement is provider-specific: SQLite spells it <c>max(a, b)</c>, PostgreSQL
/// <c>GREATEST(a, b)</c>.
///
/// WHAT THAT DOES *NOT* CHANGE: the reset rule. A reported count that DROPPED means a Director restarted that
/// session id and is counting fresh from zero, so the whole current count is new activity. That rule is
/// unchanged and it lives where it always did - in the FOLD, against the in-memory mirror, before a batch is
/// ever built (GatewayInputStatsAggregator.FoldLocked). The stored row is the durable floor the mirror is
/// rebuilt from at startup, not the thing the delta is computed against at runtime.
///
/// The append-only tables (the four delta tables) and the identity mints are ordinary Entity Framework
/// inserts. That is not an inconsistency: an INSERT of a brand new row is not a read-modify-write and has no
/// lost update to lose. Change tracking is used exactly where a row is created and never re-read, and nowhere
/// else.
///
/// Threading: NOT thread-safe by itself, and it does not need to be - callers serialise (the aggregator holds
/// its own lock, and one batch is one tenant). Two INDEPENDENT writers on two processes against one database
/// are exactly the case the upserts above are for, and that is the case the interleaved-writer proof drives.
/// </summary>
internal sealed class GatewayStatsWriter
{
    private readonly IDbContextFactory<GatewayStatsDbContext> _contexts;
    private long _statements;

    /// <summary>Every write statement this writer has executed. Counts one per row written plus the prune
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
    /// Write everything <paramref name="batch"/> collected, in ONE transaction, and return the surrogate ids
    /// minted for its new identities so the caller can advance its mirror AFTER the commit.
    ///
    /// An EMPTY batch - an idle poll - writes nothing, creates no context and does not even open a
    /// transaction.
    /// </summary>
    /// <param name="batch">One tenant's collected observation.</param>
    /// <param name="resolveKnownIdentity">Resolves a display spelling this batch did NOT mint to the id the
    /// caller's identity mirror already holds for it, within the batch's tenant. It is the caller's map
    /// because the map is what DECIDES identity - a case-insensitive comparer this store deliberately never
    /// asks a database to reproduce.</param>
    /// <param name="beforeCommit">Test seam, and only a test seam: run after every statement of the batch has
    /// executed and before the transaction commits. The interleaved-writer proof needs to hold one
    /// transaction open at a known point to make the race deterministic instead of a matter of timing luck;
    /// production passes null and this is a no-op.</param>
    public IReadOnlyDictionary<IdentityKind, IReadOnlyDictionary<string, long>> Commit(
        StatsWriteBatch batch,
        Func<string, IdentityKind, long> resolveKnownIdentity,
        Action? beforeCommit = null)
    {
        var minted = new Dictionary<IdentityKind, IReadOnlyDictionary<string, long>>();
        if (batch.IsEmpty) return minted;

        var tenant = batch.Tenant.Value;
        using var ctx = _contexts.CreateDbContext();
        var sql = new StatsUpsertSql(ctx);
        using var tx = ctx.Database.BeginTransaction();

        // agents_since is per tenant (MTR-08): (tenant, name) is the key, and the stamp is written ONCE and
        // never moved. Insert-if-absent, so a tenant that already has a start keeps its own earliest one even
        // when a second writer's mirror had not seen it yet.
        if (batch.StampAgentsSince is not null)
            Execute(ctx, sql.InsertMetaIfAbsent, tenant, GatewayStatsAggregatorKeys.AgentsSince, batch.StampAgentsSince);

        var freshIds = MintIdentities(ctx, tenant, batch);
        foreach (var (kind, map) in freshIds) minted[kind] = map;

        long Resolve(string display, IdentityKind kind) =>
            freshIds[kind].TryGetValue(display, out var fresh) ? fresh : resolveKnownIdentity(display, kind);

        // An absent model resolves to nothing at all - SQL NULL, rather than a sentinel id a later reader
        // could mistake for a real model.
        long? ResolveModel(string? display) => display is null ? null : Resolve(display, IdentityKind.Model);

        // ---- The append-only tables. An insert of a new row, never a read-modify-write. ----

        foreach (var r in batch.Rows)
            ctx.StatDeltas.Add(new StatDeltaEntity
            {
                Tenant = tenant,
                HourUtc = r.Hour,
                SessionId = r.SessionId,
                Modality = r.Modality,
                Surface = r.Surface,
                IsVoice = r.IsVoice,
                RepoId = Resolve(r.Repo, IdentityKind.Repo),
                CheckoutId = Resolve(r.Checkout, IdentityKind.Checkout),
                ModelId = ResolveModel(r.Model),
                Wingman = r.Wingman,
                Turns = r.Turns,
                Chars = r.Chars,
            });

        foreach (var a in batch.AgentRows)
            ctx.AgentDeltas.Add(new AgentDeltaEntity
            {
                Tenant = tenant,
                AgentId = Resolve(a.Agent, IdentityKind.Agent),
                IsVoice = a.IsVoice,
                Turns = a.Turns,
                Chars = a.Chars,
            });

        foreach (var a in batch.AgentDrivenRows)
            ctx.AgentDrivenDeltas.Add(new AgentDrivenDeltaEntity
            {
                Tenant = tenant,
                AgentId = Resolve(a.Agent, IdentityKind.Agent),
                Turns = a.Turns,
                Chars = a.Chars,
            });

        foreach (var r in batch.TokenRows)
            ctx.TokenDeltas.Add(new TokenDeltaEntity
            {
                Tenant = tenant,
                HourUtc = r.Hour,
                ModelId = ResolveModel(r.Model),
                InputTokens = r.Input,
                OutputTokens = r.Output,
                CacheReadTokens = r.CacheRead,
                CacheCreationTokens = r.CacheCreation,
            });

        var deltaRows = batch.Rows.Count + batch.AgentRows.Count + batch.AgentDrivenRows.Count + batch.TokenRows.Count;
        if (deltaRows > 0)
        {
            ctx.SaveChanges();
            Interlocked.Add(ref _statements, deltaRows);
        }

        // ---- The high-water tables. Explicit upsert, comparing, never overwriting. ----

        foreach (var h in batch.HighWater)
            Execute(ctx, sql.UpsertSessionHighWater, tenant, h.SessionId, h.Modality, h.Surface, h.Turns, h.Chars);

        foreach (var h in batch.AgentDrivenHighWater)
            Execute(ctx, sql.UpsertAgentDrivenHighWater, tenant, h.SessionId, h.Turns, h.Chars);

        foreach (var h in batch.TokenHighWater)
            Execute(ctx, sql.UpsertTokenHighWater, tenant, h.SessionId, h.Input, h.Output, h.CacheRead, h.CacheCreation);

        // ---- The membership sets. Insert-if-absent, never a read-then-insert. ----

        foreach (var sid in batch.NewWingmanSessions)
            Execute(ctx, sql.InsertWingmanSessionIfAbsent, tenant, sid);

        foreach (var sid in batch.NewSeeded)
            Execute(ctx, sql.InsertAgentsSeededIfAbsent, tenant, sid);

        foreach (var (display, sessionId, kind) in batch.NewIdentitySessions)
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

        if (batch.Rows.Count > 0 || batch.TokenRows.Count > 0)
            Prune(ctx, sql, tenant, batch.NowUtc);

        beforeCommit?.Invoke();
        tx.Commit();
        return minted;
    }

    /// <summary>
    /// Drop one session's high-water rows for one tenant. Its contribution stays in the totals - it was
    /// folded in as it happened; dropping the watermark just stops the table growing without bound.
    /// </summary>
    /// <returns>Whether a row was deleted, so the caller can keep its mirror and the store in step.</returns>
    public bool DeleteSessionHighWater(string tenant, string sessionId)
    {
        using var ctx = _contexts.CreateDbContext();
        return Execute(ctx, new StatsUpsertSql(ctx).DeleteSessionHighWater, tenant, sessionId) > 0;
    }

    /// <summary>Drop one session's token high-water row for one tenant. Cleaned up on its own, not under the
    /// session_highwater guard: a session has both, and dropping only one would leave the other's map growing
    /// without bound.</summary>
    public bool DeleteTokenHighWater(string tenant, string sessionId)
    {
        using var ctx = _contexts.CreateDbContext();
        return Execute(ctx, new StatsUpsertSql(ctx).DeleteTokenHighWater, tenant, sessionId) > 0;
    }

    // Mint a surrogate id for each display spelling this batch saw for the first time. An INSERT of a new row
    // whose key the database generates, so the id comes back on the tracked entity - no last-insert-rowid, no
    // RETURNING clause to spell two ways, and nothing read before it is written.
    private Dictionary<IdentityKind, Dictionary<string, long>> MintIdentities(
        GatewayStatsDbContext ctx, string tenant, StatsWriteBatch batch)
    {
        // Keyed with the SAME comparer as the mirror they will join, so two spellings differing only by case
        // resolve to one identity here exactly as they do there.
        var minted = new Dictionary<IdentityKind, Dictionary<string, long>>
        {
            [IdentityKind.Repo] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Agent] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Model] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Checkout] = new(StringComparer.OrdinalIgnoreCase),
        };
        if (batch.NewIdentities.Count == 0) return minted;

        var pending = new List<(string Display, IdentityKind Kind, Func<long> Id)>();
        foreach (var (display, kind) in batch.NewIdentities)
        {
            switch (kind)
            {
                case IdentityKind.Repo:
                    var repo = new RepoIdentityEntity { RepoDisplay = display, Tenant = tenant };
                    ctx.RepoIdentities.Add(repo);
                    pending.Add((display, kind, () => repo.RepoId));
                    break;
                case IdentityKind.Agent:
                    var agent = new AgentIdentityEntity { AgentDisplay = display, Tenant = tenant };
                    ctx.AgentIdentities.Add(agent);
                    pending.Add((display, kind, () => agent.AgentId));
                    break;
                case IdentityKind.Model:
                    var model = new ModelIdentityEntity { ModelDisplay = display, Tenant = tenant };
                    ctx.ModelIdentities.Add(model);
                    pending.Add((display, kind, () => model.ModelId));
                    break;
                case IdentityKind.Checkout:
                    var checkout = new CheckoutIdentityEntity { CheckoutDisplay = display, Tenant = tenant };
                    ctx.CheckoutIdentities.Add(checkout);
                    pending.Add((display, kind, () => checkout.CheckoutId));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(batch), kind, "Unknown identity kind.");
            }
        }

        ctx.SaveChanges();
        Interlocked.Add(ref _statements, pending.Count);
        foreach (var (display, kind, id) in pending) minted[kind][display] = id();
        return minted;
    }

    // Prune the working-day detail past the retention window, for ONE tenant only, inside the caller's
    // transaction.
    //
    // PER-PARTITION (MTR-08, the same class as the car-mode #1933 global-prune bug): every statement is
    // constrained to this tenant - without that, a caller's write would archive and delete EVERY tenant's
    // rows older than the cutoff, a cross-tenant mutation that violates the invariant that a caller's write
    // only ever touches its own partition. A quiet tenant's expired detail is reclaimed when THAT tenant next
    // writes; a global age-sweep, if ever wanted, is a background job, never a side effect of an unrelated
    // tenant's fold.
    //
    // Departing rows are folded into ARCHIVE rows FIRST. One row here feeds both the hourly series and the
    // all-time totals, so deleting it outright would silently shrink the all-time totals - the #1376 class of
    // failure. The fold preserves every dimension any all-time answer groups by; pruning collapses the hour
    // and the session id, and nothing else. agent_delta and agent_driven_delta carry no hour and are never
    // pruned, matching the all-time agent tally.
    private void Prune(GatewayStatsDbContext ctx, StatsUpsertSql sql, string tenant, DateTime nowUtc)
    {
        var cutoff = StatsHourKey.For(nowUtc.AddDays(-StatsHourKey.RetentionDays));
        Execute(ctx, sql.ArchiveStatDelta, tenant, GatewayStatsDatabase.ArchiveMarker, cutoff);
        Execute(ctx, sql.DeleteArchivedStatDelta, tenant, GatewayStatsDatabase.ArchiveMarker, cutoff);
        Execute(ctx, sql.ArchiveTokenDelta, tenant, GatewayStatsDatabase.ArchiveMarker, cutoff);
        Execute(ctx, sql.DeleteArchivedTokenDelta, tenant, GatewayStatsDatabase.ArchiveMarker, cutoff);
    }

    // Every raw statement passes through here so the count is honest. Positional {0} placeholders are turned
    // into real provider parameters by Entity Framework, so one statement text serves both providers and no
    // value is ever concatenated into SQL.
    private int Execute(GatewayStatsDbContext ctx, string statement, params object[] args)
    {
        var affected = ctx.Database.ExecuteSqlRaw(statement, args);
        Interlocked.Add(ref _statements, 1);
        return affected;
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
/// Shared where the dialects agree - and they agree on most of it: both SQLite and PostgreSQL spell the
/// upsert <c>INSERT ... ON CONFLICT (key) DO UPDATE SET</c> / <c>DO NOTHING</c>, and both expose the proposed
/// row as <c>excluded</c>. Provider-specific in exactly two places, both named here so nobody has to go
/// looking for a third:
///
///  1. THE COMPARISON. SQLite's two-argument <c>max(a, b)</c> is PostgreSQL's <c>GREATEST(a, b)</c>, and
///     PostgreSQL wants the existing row qualified by the table name inside the SET expression.
///  2. THE TABLE NAME. On PostgreSQL every table lives in the <c>gateway_stats</c> schema; SQLite is
///     schemaless.
///
/// A third difference is handled without a dialect branch: <c>SUM()</c> over a 64-bit column returns
/// <c>numeric</c> on PostgreSQL, so the archive fold casts explicitly - which SQLite accepts unchanged, so
/// one statement still serves both.
/// </summary>
internal sealed class StatsUpsertSql
{
    private readonly bool _postgres;
    private readonly string _prefix;

    public StatsUpsertSql(GatewayStatsDbContext ctx)
    {
        _postgres = ctx.Database.IsNpgsql();
        _prefix = _postgres ? GatewayStatsDbContext.PostgresSchema + "." : "";
        FileLog.Write($"[StatsUpsertSql] Build: provider={ctx.Database.ProviderName}, postgres={_postgres}");
    }

    private string Table(string name) => _prefix + name;

    // The high-water rule, in one place: the stored count is a FLOOR that never regresses, so whichever of
    // two concurrent writers commits second cannot push the watermark back down.
    private string RaiseTo(string table, params string[] columns) =>
        string.Join(", ", columns.Select(c => _postgres
            ? $"{c} = GREATEST({table}.{c}, excluded.{c})"
            : $"{c} = max({c}, excluded.{c})"));

    public string UpsertSessionHighWater =>
        $@"INSERT INTO {Table("session_highwater")} (tenant, session_id, modality, surface, turns, chars)
           VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}})
           ON CONFLICT (tenant, session_id, modality, surface)
           DO UPDATE SET {RaiseTo("session_highwater", "turns", "chars")}";

    public string UpsertAgentDrivenHighWater =>
        $@"INSERT INTO {Table("agent_driven_highwater")} (tenant, session_id, turns, chars)
           VALUES ({{0}}, {{1}}, {{2}}, {{3}})
           ON CONFLICT (tenant, session_id)
           DO UPDATE SET {RaiseTo("agent_driven_highwater", "turns", "chars")}";

    public string UpsertTokenHighWater =>
        $@"INSERT INTO {Table("token_highwater")} (tenant, session_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
           VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}})
           ON CONFLICT (tenant, session_id)
           DO UPDATE SET {RaiseTo("token_highwater", "input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens")}";

    public string InsertWingmanSessionIfAbsent =>
        $@"INSERT INTO {Table("wingman_session")} (tenant, session_id) VALUES ({{0}}, {{1}})
           ON CONFLICT (tenant, session_id) DO NOTHING";

    public string InsertAgentsSeededIfAbsent =>
        $@"INSERT INTO {Table("agents_seeded")} (tenant, session_id) VALUES ({{0}}, {{1}})
           ON CONFLICT (tenant, session_id) DO NOTHING";

    // repo_session and agent_session carry no tenant column at schema version 5. They are partitioned
    // INDIRECTLY: repo_id and agent_id are surrogates minted per tenant, so the pair is already
    // tenant-unique. Carried forward unchanged - adding a tenant column here is a behaviour change.
    public string InsertRepoSessionIfAbsent =>
        $@"INSERT INTO {Table("repo_session")} (repo_id, session_id) VALUES ({{0}}, {{1}})
           ON CONFLICT (repo_id, session_id) DO NOTHING";

    public string InsertAgentSessionIfAbsent =>
        $@"INSERT INTO {Table("agent_session")} (agent_id, session_id) VALUES ({{0}}, {{1}})
           ON CONFLICT (agent_id, session_id) DO NOTHING";

    // The since-stamps are written once and never moved, so insert-if-absent is the whole semantic. A second
    // writer whose mirror had not yet seen the stamp proposes its own and the earlier one stands.
    public string InsertMetaIfAbsent =>
        $@"INSERT INTO {Table("meta")} (tenant, name, value) VALUES ({{0}}, {{1}}, {{2}})
           ON CONFLICT (tenant, name) DO NOTHING";

    public string DeleteSessionHighWater =>
        $"DELETE FROM {Table("session_highwater")} WHERE tenant = {{0}} AND session_id = {{1}}";

    public string DeleteTokenHighWater =>
        $"DELETE FROM {Table("token_highwater")} WHERE tenant = {{0}} AND session_id = {{1}}";

    // model_id and checkout_id are carried through the archive fold and each MUST be in BOTH lists. Left out
    // of the SELECT the archive row would read NULL and every pruned turn would silently lose that dimension;
    // left out of the GROUP BY it would collapse different values into one row and take an arbitrary id with
    // it. Adding a dimension to this table means adding it here, in both places, or pruning quietly destroys
    // it ninety days later - long after the change that caused it. The tenant rides the fold for the same
    // reason. Both providers group NULLs together, so every unknown-model row of a bucket archives into ONE
    // row that is still honestly NULL: absence aggregates as absence.
    public string ArchiveStatDelta =>
        $@"INSERT INTO {Table("stat_delta")} (tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman, turns, chars)
           SELECT tenant, {{1}}, {{1}}, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman,
                  CAST(SUM(turns) AS BIGINT), CAST(SUM(chars) AS BIGINT)
             FROM {Table("stat_delta")}
            WHERE tenant = {{0}} AND hour_utc <> {{1}} AND hour_utc < {{2}}
            GROUP BY tenant, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman";

    public string DeleteArchivedStatDelta =>
        $@"DELETE FROM {Table("stat_delta")}
            WHERE tenant = {{0}} AND hour_utc <> {{1}} AND hour_utc < {{2}}";

    public string ArchiveTokenDelta =>
        $@"INSERT INTO {Table("token_delta")} (tenant, hour_utc, model_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
           SELECT tenant, {{1}}, model_id,
                  CAST(SUM(input_tokens) AS BIGINT), CAST(SUM(output_tokens) AS BIGINT),
                  CAST(SUM(cache_read_tokens) AS BIGINT), CAST(SUM(cache_creation_tokens) AS BIGINT)
             FROM {Table("token_delta")}
            WHERE tenant = {{0}} AND hour_utc <> {{1}} AND hour_utc < {{2}}
            GROUP BY tenant, model_id";

    public string DeleteArchivedTokenDelta =>
        $@"DELETE FROM {Table("token_delta")}
            WHERE tenant = {{0}} AND hour_utc <> {{1}} AND hour_utc < {{2}}";
}
