using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// PROOF ROW 2: interleaved writers on the statistics write path never lose an update AND never count one
/// twice, against a REAL PostgreSQL server.
///
/// WHAT THIS SUITE ASSERTS, AND WHY IT IS NOT WHAT IT USED TO ASSERT. The first version of these facts
/// checked WATERMARKS: after two writers race, is the stored high-water the higher of the two values? That
/// number was always going to be right - the raise compares, so it cannot go backwards - and it is not where
/// the store's numbers live. The numbers live in the APPEND-ONLY DELTA LEDGER, which every all-time total is
/// the sum of, and that ledger was always going to be wrong: each writer computed its growth against its OWN
/// in-memory mirror and appended that growth to the shared ledger, so two writers measuring from two stale
/// baselines appended MORE in total than the watermark ever moved. The watermark assertion passed; the totals
/// inflated on every interleave.
///
/// So the assertion carried by EVERY interleave below is the one that was missing:
///
///     AFTER ANY INTERLEAVE, THE SUM OF APPENDED DELTAS EQUALS FINAL WATERMARK MINUS INITIAL WATERMARK.
///
/// It is checked inside <see cref="InterleaveAndCommit"/> itself rather than written out beside each
/// watermark check, so no fact can be added here that forgets it. It holds because the raise statement
/// returns what the row held before it and what it holds after, and the writer appends exactly that
/// difference - the identity is by construction, not by care.
///
/// THE RACE IS DETERMINISTIC, NOT A MATTER OF TIMING LUCK. A concurrency test that has never been watched
/// failing proves nothing: it passes just as happily when the race never happened. So the interleave here is
/// arranged and OBSERVED - the first writer holds its transaction open at a known point, and the test waits
/// until PostgreSQL reports the second writer's OWN BACKEND blocked, by the first writer's backend, on a
/// row-level lock in the contested table. Counting any backend waiting on any lock is not a witness to this
/// race, and <see cref="TheRaceWitness_RefusesToCertify_AnUnrelatedLock"/> is the fact that proves the weaker
/// version certified races that never happened.
///
/// Gated behind <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> - the RESTRICTED-role, statistics-database
/// connection string that <c>scripts/pg-stats-proof-rig.ps1</c> hands out, whose role holds exactly the
/// hosted role's measured grants and nothing more. With it unset (the ordinary SQLite test run and CI) every
/// fact reports SKIPPED and nothing touches a database. Point it at a THROWAWAY local container - never at
/// the hosted database, which staging shares with production; the guard below refuses any database whose name
/// does not start with "ccpg", and the suite drops and recreates the whole gateway_stats schema.
///
/// Stand the rig up with your OWN instance and port - one instance per caller, so no two agents share a
/// server and nobody's deliberate red is somebody else's privilege change:
///   powershell -NoProfile -File scripts/pg-stats-proof-rig.ps1 -Instance w4 -Port 55434 -Verb up
/// </summary>
public sealed class GatewayStatsWritePathPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";
    private const string Session = "s-highwater";
    private const string Repo = "thefrederiksen/devthrottle";
    private const string Checkout = "D:\\ReposFred\\devthrottle";
    private const string TheAgent = "ClaudeCode";
    private static readonly TenantId Tenant = TenantId.Local;

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} (scripts/pg-stats-proof-rig.ps1) to run the write-path proof on real PostgreSQL.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    // ---- The rig -------------------------------------------------------------------------------------

    /// <summary>A context per operation over the Npgsql connection pool - the hosted shape, where a context
    /// is short-lived and the CONNECTION is what is pooled.</summary>
    private sealed class NpgsqlStatsContextFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        private readonly DbContextOptions<GatewayStatsDbContext> _options =
            new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(Connection).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// A factory pinned to ONE caller-owned connection, so the test knows exactly which server backend a
    /// writer's statements run on.
    ///
    /// That is the whole reason it exists: the race witness has to name WRITER B'S OWN BACKEND, and a pooled
    /// factory hands out whichever connection is free. With the connection pinned, the test can ask it for
    /// its backend process id BEFORE the writer starts and then, from a third connection, ask the server
    /// about that exact backend while it is blocked and unable to answer for itself.
    /// </summary>
    private sealed class PinnedConnectionFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        private readonly DbContextOptions<GatewayStatsDbContext> _options;

        public PinnedConnectionFactory(NpgsqlConnection connection) =>
            _options = new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(connection).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    private static IDbContextFactory<GatewayStatsDbContext> Factory() => new NpgsqlStatsContextFactory();

    private static NpgsqlConnection OpenPinned(out int backendPid)
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_backend_pid()";
        backendPid = Convert.ToInt32(cmd.ExecuteScalar());
        return connection;
    }

    /// <summary>
    /// Drop and recreate the statistics schema from the model. Every fact starts from an empty store, so no
    /// fact can pass on a row another one left behind.
    ///
    /// The tables are created from <see cref="GatewayStatsDbContext"/> itself rather than by a migration
    /// because the migration chain is a different worker's piece and lands separately; the SHAPE is the same
    /// model either way, which is what these facts are about. It also proves something worth having on its
    /// own: a role holding only CREATE on the database can create this schema, its tables and its indexes -
    /// including the (tenant, spelling) unique indexes the identity mints now conflict against.
    /// </summary>
    private static void ResetSchema()
    {
        GuardThrowawayDatabase();
        using var ctx = Factory().CreateDbContext();
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
        ctx.Database.ExecuteSqlRaw($"CREATE SCHEMA {GatewayStatsDbContext.PostgresSchema}");
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript());
    }

    private static void GuardThrowawayDatabase()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in the database '{database}': its name must begin " +
                $"with the throwaway prefix 'ccpg'. Point {ConnectionEnvVar} at a disposable local database " +
                "(the rig's ccpgstats_<instance>). The hosted database is shared with production and is never a test target.");
        if (Connection.Contains("supabase", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to run the write-path proof against a Supabase host. Local throwaway containers only.");
    }

    private static StatsWriteBatch NewBatch() =>
        new(Tenant, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc), "2026-07-30T12");

    /// <summary>The caller's identity mirror must never be consulted: every batch here queues the spellings it
    /// uses, so the writer resolves them itself against the store. A call to this is a real defect.</summary>
    private static long ResolveNothing(string display, IdentityKind kind) =>
        throw new InvalidOperationException($"No identity should need resolving here ({kind}: {display}).");

    // Queue the identities every delta row a bucket observation can produce needs, so a growing observation
    // can be filed without the caller's mirror. Minting is an upsert that returns the winning id, so a second
    // batch naming the same spellings simply resolves to the same ids - which is itself worth exercising on
    // every fact rather than in one place.
    private static void WithIdentities(StatsWriteBatch batch)
    {
        batch.NewIdentities.Add((Repo, IdentityKind.Repo));
        batch.NewIdentities.Add((Checkout, IdentityKind.Checkout));
        batch.NewIdentities.Add((TheAgent, IdentityKind.Agent));
    }

    private static StatsWriteBatch.BucketObservation Bucket(long reportedTurns, long reportedChars, long believedTurns, long believedChars) =>
        new(Session, "typed", "phone", false, Repo, Checkout, null, false, TheAgent,
            reportedTurns, reportedChars, believedTurns, believedChars);

    private static void Write(IDbContextFactory<GatewayStatsDbContext> factory, Action<StatsWriteBatch> fill)
    {
        var batch = NewBatch();
        WithIdentities(batch);
        fill(batch);
        new GatewayStatsWriter(factory).Commit(batch, ResolveNothing);
    }

    private static long ReadOne(IDbContextFactory<GatewayStatsDbContext> factory, Func<GatewayStatsDbContext, long> read)
    {
        using var ctx = factory.CreateDbContext();
        return read(ctx);
    }

    // ---- Proof row 2: no lost update AND no double count, on all three high-water paths ---------------

    [RequiresPostgresFact]
    public void SessionHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.Buckets.Add(Bucket(5, 50, 0, 0)));

        InterleaveAndCommit(
            factory,
            // Both writers last learned 5 from the store, which is what a second container's mirror holds
            // when it has not yet seen the first one's write. That is the whole stale-baseline situation.
            holdsOpen: b => b.Buckets.Add(Bucket(10, 100, 5, 50)),
            racesIt: b => b.Buckets.Add(Bucket(7, 70, 5, 50)),
            witness: (Seed: 5, Held: 10, Raced: 7),
            readWatermark: () => ReadOne(factory, ctx => ctx.SessionHighwater.Single().Turns),
            readLedger: () => ReadOne(factory, ctx => ctx.StatDeltas.Sum(r => r.Turns)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        // The higher watermark stands. Under a read-modify-write this reads 7/70 - the second writer computed
        // its new value from a row state that no longer existed by the time it wrote.
        Assert.Equal(10, row.Turns);
        Assert.Equal(100, row.Chars);
        // And the ledger holds ten turns, not seventeen. The racing writer's five-to-seven was already inside
        // the winning writer's five-to-ten; appending it as well is the double count this whole shape removes.
        Assert.Equal(10, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(100, ctx.StatDeltas.Sum(r => r.Chars));
        // The per-agent tally is built from the same difference, so it cannot disagree.
        Assert.Equal(10, ctx.AgentDeltas.Sum(r => r.Turns));
    }

    [RequiresPostgresFact]
    public void TokenHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
            Session, null, 100, 10, 5, 1, 0, 0, 0, 0)));

        InterleaveAndCommit(
            factory,
            holdsOpen: b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
                Session, null, 900, 90, 45, 9, 100, 10, 5, 1)),
            racesIt: b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
                Session, null, 300, 30, 15, 3, 100, 10, 5, 1)),
            witness: (Seed: 100, Held: 900, Raced: 300),
            readWatermark: () => ReadOne(factory, ctx => ctx.TokenHighwater.Single().InputTokens),
            readLedger: () => ReadOne(factory, ctx => ctx.TokenDeltas.Sum(r => r.InputTokens)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.TokenHighwater.Single();
        Assert.Equal(900, row.InputTokens);
        Assert.Equal(90, row.OutputTokens);
        Assert.Equal(45, row.CacheReadTokens);
        Assert.Equal(9, row.CacheCreationTokens);
        // Every one of the four scalars is judged on its own, so every one of them has to hold the identity.
        Assert.Equal(900, ctx.TokenDeltas.Sum(r => r.InputTokens));
        Assert.Equal(90, ctx.TokenDeltas.Sum(r => r.OutputTokens));
        Assert.Equal(45, ctx.TokenDeltas.Sum(r => r.CacheReadTokens));
        Assert.Equal(9, ctx.TokenDeltas.Sum(r => r.CacheCreationTokens));
    }

    [RequiresPostgresFact]
    public void AgentDrivenHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 2, 20, 0, 0)));

        InterleaveAndCommit(
            factory,
            holdsOpen: b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 12, 120, 2, 20)),
            racesIt: b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 6, 60, 2, 20)),
            witness: (Seed: 2, Held: 12, Raced: 6),
            readWatermark: () => ReadOne(factory, ctx => ctx.AgentDrivenHighwater.Single().Turns),
            readLedger: () => ReadOne(factory, ctx => ctx.AgentDrivenDeltas.Sum(r => r.Turns)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.AgentDrivenHighwater.Single();
        Assert.Equal(12, row.Turns);
        Assert.Equal(120, row.Chars);
        Assert.Equal(12, ctx.AgentDrivenDeltas.Sum(r => r.Turns));
        Assert.Equal(120, ctx.AgentDrivenDeltas.Sum(r => r.Chars));
    }

    /// <summary>
    /// A RESET observed against CURRENT state is still counted in full - and the stored row comes DOWN with
    /// it, which is the half that used to be missing.
    ///
    /// A reported count below the stored watermark is two different events wearing one face: a Director that
    /// restarted this session id and is counting again from zero, or a writer whose baseline another writer has
    /// already overtaken. The fold cannot tell them apart, and the store used to be asked to do both jobs at
    /// once - it kept the floor (right for the stale reader) while the fold counted the drop as fresh activity
    /// (right for the restart). The two answers then disagreed about the same row, and the disagreement
    /// surfaced on the next Gateway restart, when the mirror is rebuilt from the row.
    ///
    /// Now the writer sends the baseline it believed, as evidence, and the statement rules. Here the belief is
    /// CURRENT, so the drop is a real restart: the whole of the new count is new activity and the row adopts
    /// it. The stale case is the one the interleave facts above exercise, and it rules the other way.
    /// </summary>
    [RequiresPostgresFact]
    public void ARestartObservedAgainstCurrentState_CountsInFull_AndBringsTheStoredRowDownWithIt()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.Buckets.Add(Bucket(10, 300, 0, 0)));

        // Believing 10 and reporting 3, with the row itself at 10: the writer is up to date and the session
        // has genuinely started over.
        Write(factory, b => b.Buckets.Add(Bucket(3, 40, 10, 300)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        Assert.Equal(3, row.Turns);
        Assert.Equal(40, row.Chars);
        // Thirteen turns really happened: ten before the restart and three after.
        Assert.Equal(13, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(340, ctx.StatDeltas.Sum(r => r.Chars));
    }

    /// <summary>
    /// The same property without the arranged interleave: many writers, many rounds, all on ONE row, running
    /// in whatever order the server gives them, each keeping its own mirror and updating it ONLY from what the
    /// commit reports - which is exactly how the aggregator behaves.
    ///
    /// The reported counts come from one shared monotone counter because that is what the world looks like:
    /// every writer is reading the SAME session's cumulative tally, so no two of them can honestly see it go
    /// backwards. (Eight writers each reporting their own unrelated ascending series would be modelling eight
    /// different sessions through one row, and every disagreement between them would be read as a restart.)
    ///
    /// This cannot prove the race happened, so it is not the proof - the deterministic facts above are. It
    /// catches an implementation that only survives the one interleaving those arrange.
    /// </summary>
    [RequiresPostgresFact]
    public void SessionHighWater_ManyConcurrentWriters_LeaveTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;
        const int rounds = 25;

        var reported = 0L;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                // This thread's mirror of the row, advanced ONLY from what a commit reports.
                long believedTurns = 0, believedChars = 0;
                start.SignalAndWait();
                for (var round = 1; round <= rounds; round++)
                {
                    var value = Interlocked.Increment(ref reported);
                    var batch = NewBatch();
                    WithIdentities(batch);
                    batch.Buckets.Add(Bucket(value, value * 10, believedTurns, believedChars));
                    var committed = writer.Commit(batch, ResolveNothing);
                    var stored = committed.SessionHighWater.Single();
                    believedTurns = stored.Turns;
                    believedChars = stored.Chars;
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");
        Assert.Empty(failures);

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        var highest = (long)writers * rounds;
        Assert.Equal(highest, row.Turns);
        Assert.Equal(highest * 10, row.Chars);
        // The invariant, over two hundred racing commits: what the ledger gained is what the watermark moved.
        Assert.Equal(highest, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(highest * 10, ctx.StatDeltas.Sum(r => r.Chars));
    }

    // ---- Identity: one spelling, one id, however many writers mint it at once ------------------------

    /// <summary>
    /// Concurrent mints of ONE spelling settle on ONE id, and every writer learns which id that is.
    ///
    /// The old mint inserted a row and took the id the insert generated, which is only the right id when
    /// nobody else was minting the same spelling at the same moment. When somebody was, that tenant's turns
    /// split silently across two surrogate ids and its repository appeared twice with half its work each -
    /// a wrong number that looks exactly like a right one. The mint is now an upsert against the (tenant,
    /// spelling) unique index that RETURNS the surviving id, so a writer that loses the race is told so.
    /// </summary>
    [RequiresPostgresFact]
    public void ConcurrentMintsOfOneSpelling_AllResolveToTheSameId()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var ids = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                var batch = NewBatch();
                batch.NewIdentities.Add((Repo, IdentityKind.Repo));
                start.SignalAndWait();
                var committed = writer.Commit(batch, ResolveNothing);
                ids.Enqueue(committed.Identities[IdentityKind.Repo][Repo]);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        Assert.Empty(failures);
        Assert.Equal(writers, ids.Count);
        Assert.Single(ids.Distinct());
        using var ctx = factory.CreateDbContext();
        var row = Assert.Single(ctx.RepoIdentities);
        Assert.Equal(row.RepoId, ids.First());
    }

    // ---- The membership sets and the first-fold back-fill --------------------------------------------

    [RequiresPostgresFact]
    public void MembershipWrites_AreInsertIfAbsent_UnderConcurrency()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                start.SignalAndWait();
                var batch = NewBatch();
                batch.NewWingmanSessions.Add(Session);
                batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>()));
                batch.StampAgentsSince = "2026-07-30T12:00:00.0000000Z";
                writer.Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        // A read-then-insert would have raced two writers into the same key and raised a unique violation on
        // one of them; ON CONFLICT DO NOTHING simply leaves the row that is already there.
        Assert.Empty(failures);
        using var ctx = factory.CreateDbContext();
        Assert.Equal(1, ctx.WingmanSessions.Count());
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal(1, ctx.Meta.Count());
    }

    /// <summary>
    /// The first-fold back-fill (issue #1633) is attributed ONCE, by whichever writer's insert claimed the
    /// mark - not once per writer that found an unmarked mirror.
    ///
    /// Eight writers all first-folding one session all find it unseeded, because none of their mirrors has
    /// the mark yet. Attributing on that belief multiplies the agent's standing count by however many
    /// containers happen to be running. The mark is insert-if-absent and the statement reports whether THIS
    /// insert created the row; only that writer attributes.
    /// </summary>
    [RequiresPostgresFact]
    public void TheFirstFoldBackfill_IsAttributedOnce_HoweverManyWritersFirstFoldTheSession()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                var batch = NewBatch();
                batch.NewIdentities.Add((TheAgent, IdentityKind.Agent));
                batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>
                {
                    new(TheAgent, false, 40, 400),
                }));
                start.SignalAndWait();
                writer.Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        Assert.Empty(failures);
        using var ctx = factory.CreateDbContext();
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal(40, ctx.AgentDeltas.Sum(r => r.Turns));
        Assert.Equal(400, ctx.AgentDeltas.Sum(r => r.Chars));
    }

    // ---- Retention: the rows archived are the rows the statement removed -----------------------------

    /// <summary>
    /// Pruning conserves the all-time totals: the expired detail leaves and its contribution stays, folded
    /// into archive rows that keep every dimension an all-time answer groups by.
    ///
    /// The archive is now folded from the rows the DELETE itself returned rather than from a separate read of
    /// the same predicate. The two-statement shape read the table twice, so anything committed between the
    /// two reads was deleted having never been archived - a silent shrink of the all-time totals, surfacing
    /// ninety days after the write that caused it. Here the set cannot differ from itself.
    /// </summary>
    [RequiresPostgresFact]
    public void Pruning_ArchivesEveryExpiredRow_AndKeepsTheAllTimeTotals()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);
        var now = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

        // Two hours of detail, both far outside the retention window, plus one inside it.
        var expired = new[] { now.AddDays(-200), now.AddDays(-150) };
        var repoId = 0L;
        foreach (var hour in expired.Append(now))
        {
            var batch = new StatsWriteBatch(Tenant, now, StatsHourKey.For(hour));
            WithIdentities(batch);
            batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
                "s-" + StatsHourKey.For(hour), "typed", "phone", false, Repo, Checkout, null, false, TheAgent,
                7, 70, 0, 0));
            var committed = writer.Commit(batch, ResolveNothing);
            if (committed.Identities[IdentityKind.Repo].TryGetValue(Repo, out var id)) repoId = id;
        }

        using var ctx = factory.CreateDbContext();
        // Twenty-one turns went in and twenty-one turns are still there, whatever shape the rows are in now.
        Assert.Equal(21, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(210, ctx.StatDeltas.Sum(r => r.Chars));
        // The expired hours are gone as HOURS - they no longer name an hour of the working day.
        Assert.Empty(ctx.StatDeltas.Where(r => r.HourUtc != GatewayStatsDatabase.ArchiveMarker
                                            && r.HourUtc != StatsHourKey.For(now)).ToList());
        // And every turn that left is still counted, on rows that carry every dimension they arrived with.
        // (Each prune adds its own archive row rather than merging into an existing one - archive rows are
        // excluded from the sweep by their marker hour, so they are never re-read. Two expired hours pruned on
        // two different writes therefore leave two archive rows, and the all-time answers sum them.)
        var archived = ctx.StatDeltas.Where(r => r.HourUtc == GatewayStatsDatabase.ArchiveMarker).ToList();
        Assert.NotEmpty(archived);
        Assert.Equal(14, archived.Sum(r => r.Turns));
        Assert.Equal(140, archived.Sum(r => r.Chars));
        Assert.All(archived, r =>
        {
            Assert.Equal(repoId, r.RepoId);
            Assert.NotNull(r.CheckoutId);
            Assert.Null(r.ModelId);
            Assert.Equal(Tenant.Value, r.Tenant);
            Assert.Equal("typed", r.Modality);
            Assert.Equal("phone", r.Surface);
            Assert.False(r.IsVoice);
            Assert.False(r.Wingman);
        });
    }

    // ---- Idempotency: replaying one batch changes nothing after the first ----------------------------

    [RequiresPostgresFact]
    public void ReplayingOneBatchTenTimes_LeavesTheStoreAsOneReplayDid()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        // The stamp a second replay proposes is LATER than the first. Insert-if-absent means the first one
        // stands: the since-stamp is written once and never moved.
        //
        // Every replay reports the SAME counts and the same belief, which is what a re-read of an unchanged
        // roster looks like from a writer whose mirror is already current. The first replay is the only one
        // that changes anything, and the delta ledger says so - which is a stronger statement than the
        // watermark, since the ledger is append-only and has nothing to hide a repeat behind.
        for (var replay = 0; replay < 10; replay++)
        {
            var batch = NewBatch();
            WithIdentities(batch);
            batch.Buckets.Add(Bucket(9, 90, replay == 0 ? 0 : 9, replay == 0 ? 0 : 90));
            batch.Tokens.Add(new StatsWriteBatch.TokenObservation(Session, null, 500, 50, 25, 5,
                replay == 0 ? 0 : 500, replay == 0 ? 0 : 50, replay == 0 ? 0 : 25, replay == 0 ? 0 : 5));
            batch.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 4, 40,
                replay == 0 ? 0 : 4, replay == 0 ? 0 : 40));
            batch.NewWingmanSessions.Add(Session);
            batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>()));
            batch.StampAgentsSince = $"2026-07-30T12:0{replay}:00.0000000Z";
            writer.Commit(batch, ResolveNothing);
        }

        using var ctx = factory.CreateDbContext();
        var session = ctx.SessionHighwater.Single();
        Assert.Equal(9, session.Turns);
        Assert.Equal(90, session.Chars);
        var token = ctx.TokenHighwater.Single();
        Assert.Equal(500, token.InputTokens);
        Assert.Equal(5, token.CacheCreationTokens);
        var driven = ctx.AgentDrivenHighwater.Single();
        Assert.Equal(4, driven.Turns);
        Assert.Equal(1, ctx.WingmanSessions.Count());
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal("2026-07-30T12:00:00.0000000Z", ctx.Meta.Single().Value);

        // Nine turns, once, however many times the batch was replayed - because after the first replay the
        // raise statement says the row did not move, and a row that did not move contributes no delta.
        Assert.Equal(9, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(500, ctx.TokenDeltas.Sum(r => r.InputTokens));
        Assert.Equal(4, ctx.AgentDrivenDeltas.Sum(r => r.Turns));
        // Repeated mints of the same spellings settle on one row each, not ten.
        Assert.Single(ctx.RepoIdentities);
        Assert.Single(ctx.CheckoutIdentities);
        Assert.Single(ctx.AgentIdentities);
    }

    // ---- A whole batch, on PostgreSQL, with identities minted and resolved ---------------------------

    [RequiresPostgresFact]
    public void AFullBatch_MintsIdentities_AndFilesEveryRowUnderItsTenant()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        var batch = NewBatch();
        WithIdentities(batch);
        batch.NewIdentities.Add(("claude-opus-5", IdentityKind.Model));
        batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
            Session, "typed", "phone", false, Repo, Checkout, "claude-opus-5", false, TheAgent, 3, 60, 0, 0));
        batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
            Session, "voice", "phone", true, Repo, Checkout, null, true, TheAgent, 1, 10, 0, 0));
        batch.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 2, 20, 0, 0));
        batch.Tokens.Add(new StatsWriteBatch.TokenObservation(Session, "claude-opus-5", 500, 50, 25, 5, 0, 0, 0, 0));
        batch.NewIdentitySessions.Add((Repo, Session, IdentityKind.Repo));
        batch.NewIdentitySessions.Add((TheAgent, Session, IdentityKind.Agent));

        var committed = writer.Commit(batch, ResolveNothing);

        Assert.Equal(Repo, committed.Identities[IdentityKind.Repo].Keys.Single());
        using var ctx = factory.CreateDbContext();
        Assert.Equal(2, ctx.StatDeltas.Count());
        Assert.All(ctx.StatDeltas.ToList(), r => Assert.Equal(Tenant.Value, r.Tenant));
        // The model a Director never named is a real NULL, never an identity spelled "".
        Assert.Single(ctx.StatDeltas.Where(r => r.ModelId == null));
        Assert.Single(ctx.ModelIdentities);
        Assert.Equal(committed.Identities[IdentityKind.Repo].Values.Single(), ctx.RepoSessions.Single().RepoId);
        Assert.Equal(committed.Identities[IdentityKind.Agent].Values.Single(), ctx.AgentSessions.Single().AgentId);
        Assert.Equal(2, ctx.AgentDeltas.Count());
        Assert.Single(ctx.AgentDrivenDeltas);
        Assert.Single(ctx.TokenDeltas);
    }

    // ---- The arranged, observed interleave ----------------------------------------------------------

    /// <summary>
    /// Run two writers into one row in a KNOWN order: <paramref name="holdsOpen"/> executes its statement and
    /// holds the transaction open; <paramref name="racesIt"/> then runs and blocks on the row lock; only once
    /// PostgreSQL reports that block does the first writer commit.
    ///
    /// This is what makes the fact deterministic rather than a matter of timing. It is also why the wait is
    /// an OBSERVATION rather than a sleep: if the second writer never reaches the lock, the interleave never
    /// happened and this throws instead of letting a green through.
    ///
    /// AND IT IS WHERE THE LEDGER INVARIANT LIVES, so that every interleave carries it and no future fact can
    /// be written here that checks only the watermark - which is the mistake that let a real double count sit
    /// under a green suite. Both writers get their OWN pinned connection so the witness can name a backend.
    /// </summary>
    private static void InterleaveAndCommit(
        IDbContextFactory<GatewayStatsDbContext> factory,
        Action<StatsWriteBatch> holdsOpen,
        Action<StatsWriteBatch> racesIt,
        (long Seed, long Held, long Raced) witness,
        Func<long> readWatermark,
        Func<long> readLedger)
    {
        // REFUSE A FIXTURE THAT COULD NOT SHOW THE FAILURE, rather than trusting whoever wrote it to have
        // thought about it - the author is always the last person able to see the gap in their own fixture.
        // Three numbers have to be ordered for a lost update to be VISIBLE here:
        //   raced < held   - so the losing writer's value is distinguishable from the winning one at all;
        //                    equal values would read identically whether the update was lost or kept.
        //   seed  < raced  - so a lost update is also distinguishable from the second writer having done
        //                    nothing, which is a different defect with the same reading. It is also what makes
        //                    the DOUBLE COUNT visible: the racing writer's growth from the seed genuinely
        //                    overlaps the winning writer's, so appending both is an inflation the ledger
        //                    invariant below can see.
        Assert.True(witness.Seed < witness.Raced && witness.Raced < witness.Held,
            $"This fixture cannot show a lost update: seed={witness.Seed}, raced={witness.Raced}, " +
            $"held={witness.Held}. The racing value must sit strictly between the seed and the held value, " +
            "or the assertion reads the same whether the update was lost or kept.");

        // And the row must actually be there: a race to UPDATE an existing row and a race to INSERT a new
        // one are different code paths, and this fact is about the first.
        var watermarkBefore = readWatermark();
        Assert.Equal(witness.Seed, watermarkBefore);
        var ledgerBefore = readLedger();

        using var firstConnection = OpenPinned(out var firstPid);
        using var secondConnection = OpenPinned(out var secondPid);
        var firstFactory = new PinnedConnectionFactory(firstConnection);
        var secondFactory = new PinnedConnectionFactory(secondConnection);

        using var firstHasWritten = new ManualResetEventSlim(false);
        Exception? secondFailure = null;
        var second = new Thread(() =>
        {
            try
            {
                if (!firstHasWritten.Wait(TimeSpan.FromMinutes(1)))
                    throw new InvalidOperationException("The first writer never signalled that it had written.");
                var batch = NewBatch();
                WithIdentities(batch);
                racesIt(batch);
                new GatewayStatsWriter(secondFactory).Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { secondFailure = ex; }
        });
        second.Start();

        var held = NewBatch();
        WithIdentities(held);
        holdsOpen(held);
        new GatewayStatsWriter(firstFactory).Commit(held, ResolveNothing, beforeCommit: () =>
        {
            firstHasWritten.Set();
            WaitUntilTheSecondWriterIsBlockedByTheFirst(secondPid, firstPid);
        });

        Assert.True(second.Join(TimeSpan.FromMinutes(2)), "the second writer never finished");
        if (secondFailure is not null)
            throw new InvalidOperationException("The second writer failed: " + secondFailure.Message, secondFailure);

        // THE ASSERTION THAT REPLACES WHAT THIS ROW MEANS. Whatever the two writers proposed and in whatever
        // order they landed, the append-only ledger gained exactly what the watermark moved - no more (which
        // would be the double count) and no less (which would be a lost turn).
        var watermarkAfter = readWatermark();
        var ledgerAfter = readLedger();
        Assert.Equal(watermarkAfter - watermarkBefore, ledgerAfter - ledgerBefore);
    }

    /// <summary>
    /// Wait, on a THIRD connection, until the second writer's OWN backend is blocked BY the first writer's
    /// backend on a row-level lock in this schema. That moment is the interleave.
    ///
    /// EVERY CLAUSE OF THIS QUERY IS LOAD BEARING, because the version it replaces was not a witness at all:
    /// it counted any backend in the database waiting on any lock, so an unrelated advisory lock taken by
    /// anything else on the server made it certify a race that never happened. What is asserted now:
    ///
    ///   pid = the second writer's backend      not "somebody", but the writer this fact is about.
    ///   NOT granted                            it is waiting, not holding.
    ///   locktype in (transactionid, tuple)     a ROW-level wait - the shape a conflicting upsert produces.
    ///                                          Advisory, relation and object locks are excluded by name.
    ///   blocked by the first writer's backend  it is waiting for THIS transaction, not some third party.
    ///
    /// The specific ROW follows from the fixture rather than from the lock catalogue, which does not name a
    /// tuple reliably across server versions. Every fact using this helper contests exactly ONE row: the
    /// caller's <c>readWatermark</c> reads it with <c>Single()</c>, which throws if the contested table holds
    /// anything other than that one row, and that read happens before the interleave is arranged. One row in
    /// the table plus a row-level wait between these two named backends leaves nothing else it could be
    /// blocked on.
    /// </summary>
    private static void WaitUntilTheSecondWriterIsBlockedByTheFirst(int blockedPid, int blockerPid)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            if (IsBlockedBy(blockedPid, blockerPid)) return;
            Thread.Sleep(25);
        }

        throw new InvalidOperationException(
            $"Backend {blockedPid} (the second writer) never blocked on a row-level lock held by backend " +
            $"{blockerPid} (the first writer), so the two never actually interleaved. This fact proves " +
            "nothing unless the race really happens, so it fails rather than reporting a green.");
    }

    private static bool IsBlockedBy(int blockedPid, int blockerPid)
    {
        using var ctx = Factory().CreateDbContext();
        return ctx.Database.SqlQueryRaw<long>(
            @"SELECT count(*) AS ""Value"" FROM pg_locks blocked
               WHERE blocked.pid = {0}
                 AND NOT blocked.granted
                 AND blocked.locktype IN ('transactionid', 'tuple')
                 AND {1} = ANY (pg_blocking_pids(blocked.pid))", blockedPid, blockerPid).Single() > 0;
    }

    /// <summary>
    /// VALIDATE THE DETECTOR BEFORE TRUSTING ITS VERDICTS. The witness must NOT fire for a backend that is
    /// blocked on something else, which is precisely how its predecessor - "is any backend in this database
    /// waiting on any lock?" - certified interleaves that never happened.
    ///
    /// Here one connection takes an advisory lock and a second blocks on it. That is a real, observable block
    /// between two real backends; the only thing wrong with it is that it is not the race this suite is about.
    /// The witness has to say no.
    /// </summary>
    [RequiresPostgresFact]
    public void TheRaceWitness_RefusesToCertify_AnUnrelatedLock()
    {
        ResetSchema();
        using var holder = OpenPinned(out var holderPid);
        using var waiter = OpenPinned(out var waiterPid);

        using (var take = holder.CreateCommand())
        {
            take.CommandText = "SELECT pg_advisory_lock(918273645)";
            take.ExecuteScalar();
        }

        var blocked = new Thread(() =>
        {
            using var wait = waiter.CreateCommand();
            wait.CommandText = "SELECT pg_advisory_lock(918273645)";
            wait.ExecuteScalar();
        });
        blocked.Start();

        try
        {
            // Wait until the server really does report the waiter blocked by the holder - otherwise this fact
            // would pass by never setting up the situation it is supposed to reject.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var reallyBlocked = false;
            while (DateTime.UtcNow < deadline && !reallyBlocked)
            {
                using var ctx = Factory().CreateDbContext();
                reallyBlocked = ctx.Database.SqlQueryRaw<long>(
                    @"SELECT count(*) AS ""Value"" FROM pg_locks
                       WHERE pid = {0} AND NOT granted AND {1} = ANY (pg_blocking_pids(pid))",
                    waiterPid, holderPid).Single() > 0;
                if (!reallyBlocked) Thread.Sleep(25);
            }
            Assert.True(reallyBlocked, "the advisory-lock waiter never blocked, so this fact tested nothing");

            // The situation is real and the witness still says no, because an advisory lock is not a row.
            Assert.False(IsBlockedBy(waiterPid, holderPid));
        }
        finally
        {
            using var release = holder.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock(918273645)";
            release.ExecuteScalar();
            blocked.Join(TimeSpan.FromSeconds(30));
        }
    }
}
