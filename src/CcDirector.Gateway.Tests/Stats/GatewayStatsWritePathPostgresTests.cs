using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// PROOF ROW 2: interleaved writers on the statistics high-water paths never lose an update, against a REAL
/// PostgreSQL server.
///
/// Why this suite exists, in one paragraph, because a reviewer will otherwise ask for the read-then-save
/// version back as a simplification. The self-host statistics store was a single process holding one SQLite
/// connection under one lock, so "read the row, work out the new value, write it back" was safe by
/// construction. The hosted Gateway breaks that premise: a slot swap runs TWO containers against ONE database
/// at the same time. Under concurrent PostgreSQL a read-modify-write is a lost-update generator - both
/// writers read the same stored count, and the one that commits second writes a value computed from a state
/// that is already gone, so a watermark goes BACKWARDS and the next fold re-counts turns that were already
/// counted. Every high-water write is therefore an explicit <c>ON CONFLICT ... DO UPDATE</c> that RAISES the
/// stored value rather than overwriting it, and these facts are what say so.
///
/// THE RACE IS DETERMINISTIC, NOT A MATTER OF TIMING LUCK. A concurrency test that has never been watched
/// failing proves nothing: it passes just as happily when the race never happened. So the interleave here is
/// arranged and OBSERVED - the first writer holds its transaction open at a known point, and the test waits
/// until PostgreSQL itself reports the second writer blocked on that row's lock before letting the first one
/// commit. If the second writer never blocks, the interleave did not happen and the fact FAILS LOUD instead
/// of passing on a race that was never run.
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
    /// is short-lived and the CONNECTION is what is pooled. Each writer in these facts gets its own, which is
    /// what makes two writers here stand for two containers there.</summary>
    private sealed class NpgsqlStatsContextFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        private readonly DbContextOptions<GatewayStatsDbContext> _options =
            new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(Connection).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    private static IDbContextFactory<GatewayStatsDbContext> Factory() => new NpgsqlStatsContextFactory();

    /// <summary>
    /// Drop and recreate the statistics schema from the model. Every fact starts from an empty store, so no
    /// fact can pass on a row another one left behind.
    ///
    /// The tables are created from <see cref="GatewayStatsDbContext"/> itself rather than by a migration
    /// because the migration chain is a different worker's piece and lands separately; the SHAPE is the same
    /// model either way, which is what these facts are about. It also proves something worth having on its
    /// own: a role holding only CREATE on the database can create this schema and its tables.
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

    private static long ResolveNothing(string display, IdentityKind kind) =>
        throw new InvalidOperationException($"No identity should need resolving here ({kind}: {display}).");

    private static long ReadOne(IDbContextFactory<GatewayStatsDbContext> factory, Func<GatewayStatsDbContext, long> read)
    {
        using var ctx = factory.CreateDbContext();
        return read(ctx);
    }

    private static void Write(IDbContextFactory<GatewayStatsDbContext> factory, Action<StatsWriteBatch> fill)
    {
        var batch = NewBatch();
        fill(batch);
        new GatewayStatsWriter(factory).Commit(batch, ResolveNothing);
    }

    // ---- Proof row 2: no lost update on any of the three high-water paths ----------------------------

    [RequiresPostgresFact]
    public void SessionHighWater_InterleavedWriters_DoNotLoseAnUpdate()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.HighWater.Add((Session, "typed", "phone", 5, 50)));

        InterleaveAndCommit(
            factory,
            holdsOpen: b => b.HighWater.Add((Session, "typed", "phone", 10, 100)),
            racesIt: b => b.HighWater.Add((Session, "typed", "phone", 7, 70)),
            witness: (Seed: 5, Held: 10, Raced: 7),
            readWatermark: () => ReadOne(factory, ctx => ctx.SessionHighwater.Single().Turns));

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        // The higher watermark stands. Under a read-modify-write this reads 7/70 - the second writer computed
        // its new value from a row state that no longer existed by the time it wrote.
        Assert.Equal(10, row.Turns);
        Assert.Equal(100, row.Chars);
    }

    [RequiresPostgresFact]
    public void TokenHighWater_InterleavedWriters_DoNotLoseAnUpdate()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.TokenHighWater.Add((Session, 100, 10, 5, 1)));

        InterleaveAndCommit(
            factory,
            holdsOpen: b => b.TokenHighWater.Add((Session, 900, 90, 45, 9)),
            racesIt: b => b.TokenHighWater.Add((Session, 300, 30, 15, 3)),
            witness: (Seed: 100, Held: 900, Raced: 300),
            readWatermark: () => ReadOne(factory, ctx => ctx.TokenHighwater.Single().InputTokens));

        using var ctx = factory.CreateDbContext();
        var row = ctx.TokenHighwater.Single();
        Assert.Equal(900, row.InputTokens);
        Assert.Equal(90, row.OutputTokens);
        Assert.Equal(45, row.CacheReadTokens);
        Assert.Equal(9, row.CacheCreationTokens);
    }

    [RequiresPostgresFact]
    public void AgentDrivenHighWater_InterleavedWriters_DoNotLoseAnUpdate()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.AgentDrivenHighWater.Add((Session, 2, 20)));

        InterleaveAndCommit(
            factory,
            holdsOpen: b => b.AgentDrivenHighWater.Add((Session, 12, 120)),
            racesIt: b => b.AgentDrivenHighWater.Add((Session, 6, 60)),
            witness: (Seed: 2, Held: 12, Raced: 6),
            readWatermark: () => ReadOne(factory, ctx => ctx.AgentDrivenHighwater.Single().Turns));

        using var ctx = factory.CreateDbContext();
        var row = ctx.AgentDrivenHighwater.Single();
        Assert.Equal(12, row.Turns);
        Assert.Equal(120, row.Chars);
    }

    /// <summary>
    /// The same property without the arranged interleave: many writers, many rounds, all on ONE row, running
    /// at whatever order the server happens to give them. The stored watermark must end at the highest value
    /// anybody wrote. This is the unstaged companion to the deterministic facts above - it cannot prove the
    /// race happened, so it is not the proof, but it does catch an implementation that only survives the one
    /// interleaving the deterministic test arranges.
    /// </summary>
    [RequiresPostgresFact]
    public void SessionHighWater_ManyConcurrentWriters_EndAtTheHighestValue()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;
        const int rounds = 25;

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(w => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                start.SignalAndWait();
                for (var round = 1; round <= rounds; round++)
                {
                    var value = w * rounds + round;
                    var batch = NewBatch();
                    batch.HighWater.Add((Session, "typed", "phone", value, value * 10));
                    writer.Commit(batch, ResolveNothing);
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");
        Assert.Empty(failures);

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        var highest = (writers - 1) * rounds + rounds;
        Assert.Equal(highest, row.Turns);
        Assert.Equal(highest * 10, row.Chars);
    }

    // ---- The membership sets: insert-if-absent, concurrently, without an error and without a duplicate ----

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
                batch.NewSeeded.Add(Session);
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

    // ---- Idempotency: replaying one batch changes nothing after the first ----------------------------

    [RequiresPostgresFact]
    public void ReplayingOneBatchTenTimes_LeavesTheUpsertAndMembershipTablesAsOneReplayDid()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        // The stamp a second replay proposes is LATER than the first. Insert-if-absent means the first one
        // stands: the since-stamp is written once and never moved.
        for (var replay = 0; replay < 10; replay++)
        {
            var batch = NewBatch();
            batch.HighWater.Add((Session, "typed", "phone", 9, 90));
            batch.TokenHighWater.Add((Session, 500, 50, 25, 5));
            batch.AgentDrivenHighWater.Add((Session, 4, 40));
            batch.NewWingmanSessions.Add(Session);
            batch.NewSeeded.Add(Session);
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

        // The delta tables are append-only by design and are NOT idempotent under a literal replay - nothing
        // in the store deduplicates a delta, and nothing should. What protects them from a repeated roster
        // poll is the fold's high-water rule, one layer up, which emits no delta at all when nothing moved;
        // that is proven end to end in GatewayStatsWritePathTests.
        Assert.Equal(0, ctx.StatDeltas.Count());
    }

    // ---- A whole batch, on PostgreSQL, with identities minted and resolved ---------------------------

    [RequiresPostgresFact]
    public void AFullBatch_MintsIdentities_AndFilesEveryRowUnderItsTenant()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        var batch = NewBatch();
        batch.NewIdentities.Add(("thefrederiksen/devthrottle", IdentityKind.Repo));
        batch.NewIdentities.Add(("D:\\ReposFred\\devthrottle", IdentityKind.Checkout));
        batch.NewIdentities.Add(("ClaudeCode", IdentityKind.Agent));
        batch.NewIdentities.Add(("claude-opus-5", IdentityKind.Model));
        batch.Rows.Add(("2026-07-30T12", Session, "typed", "phone", false,
            "thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle", "claude-opus-5", false, 3, 60));
        batch.Rows.Add(("2026-07-30T12", Session, "voice", "phone", true,
            "thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle", null, true, 1, 10));
        batch.AgentRows.Add(("ClaudeCode", false, 3, 60));
        batch.AgentDrivenRows.Add(("ClaudeCode", 2, 20));
        batch.TokenRows.Add(("2026-07-30T12", "claude-opus-5", 500, 50, 25, 5));
        batch.NewIdentitySessions.Add(("thefrederiksen/devthrottle", Session, IdentityKind.Repo));
        batch.NewIdentitySessions.Add(("ClaudeCode", Session, IdentityKind.Agent));

        var minted = writer.Commit(batch, ResolveNothing);

        Assert.Equal("thefrederiksen/devthrottle", minted[IdentityKind.Repo].Keys.Single());
        using var ctx = factory.CreateDbContext();
        Assert.Equal(2, ctx.StatDeltas.Count());
        Assert.All(ctx.StatDeltas.ToList(), r => Assert.Equal(Tenant.Value, r.Tenant));
        // The model a Director never named is a real NULL, never an identity spelled "".
        Assert.Single(ctx.StatDeltas.Where(r => r.ModelId == null));
        Assert.Single(ctx.ModelIdentities);
        Assert.Equal(minted[IdentityKind.Repo].Values.Single(), ctx.RepoSessions.Single().RepoId);
        Assert.Equal(minted[IdentityKind.Agent].Values.Single(), ctx.AgentSessions.Single().AgentId);
        Assert.Single(ctx.AgentDeltas);
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
    /// </summary>
    private static void InterleaveAndCommit(
        IDbContextFactory<GatewayStatsDbContext> factory,
        Action<StatsWriteBatch> holdsOpen,
        Action<StatsWriteBatch> racesIt,
        (long Seed, long Held, long Raced) witness,
        Func<long> readWatermark)
    {
        // REFUSE A FIXTURE THAT COULD NOT SHOW THE FAILURE, rather than trusting whoever wrote it to have
        // thought about it - the author is always the last person able to see the gap in their own fixture.
        // Three numbers have to be ordered for a lost update to be VISIBLE here:
        //   raced < held   - so the losing writer's value is distinguishable from the winning one at all;
        //                    equal values would read identically whether the update was lost or kept.
        //   seed  < raced  - so a lost update is also distinguishable from the second writer having done
        //                    nothing, which is a different defect with the same reading.
        Assert.True(witness.Seed < witness.Raced && witness.Raced < witness.Held,
            $"This fixture cannot show a lost update: seed={witness.Seed}, raced={witness.Raced}, " +
            $"held={witness.Held}. The racing value must sit strictly between the seed and the held value, " +
            "or the assertion reads the same whether the update was lost or kept.");

        // And the row must actually be there: a race to UPDATE an existing row and a race to INSERT a new
        // one are different code paths, and this fact is about the first.
        Assert.Equal(witness.Seed, readWatermark());

        using var firstHasWritten = new ManualResetEventSlim(false);
        Exception? secondFailure = null;
        var second = new Thread(() =>
        {
            try
            {
                if (!firstHasWritten.Wait(TimeSpan.FromMinutes(1)))
                    throw new InvalidOperationException("The first writer never signalled that it had written.");
                var batch = NewBatch();
                racesIt(batch);
                new GatewayStatsWriter(factory).Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { secondFailure = ex; }
        });
        second.Start();

        var held = NewBatch();
        holdsOpen(held);
        new GatewayStatsWriter(factory).Commit(held, ResolveNothing, beforeCommit: () =>
        {
            firstHasWritten.Set();
            WaitUntilAnotherSessionIsBlockedOnALock(factory);
        });

        Assert.True(second.Join(TimeSpan.FromMinutes(2)), "the second writer never finished");
        if (secondFailure is not null)
            throw new InvalidOperationException("The second writer failed: " + secondFailure.Message, secondFailure);
    }

    // Ask the server, on a third connection, whether some other session is waiting on a lock. That is the
    // second writer parked on the row the first one is holding - the moment the interleave is real.
    private static void WaitUntilAnotherSessionIsBlockedOnALock(IDbContextFactory<GatewayStatsDbContext> factory)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            using var ctx = factory.CreateDbContext();
            var blocked = ctx.Database.SqlQueryRaw<long>(
                @"SELECT count(*) AS ""Value"" FROM pg_stat_activity
                   WHERE datname = current_database()
                     AND pid <> pg_backend_pid()
                     AND wait_event_type = 'Lock'").Single();
            if (blocked > 0) return;
            Thread.Sleep(25);
        }

        throw new InvalidOperationException(
            "No other session ever blocked on a lock, so the two writers never actually interleaved. This " +
            "fact proves nothing unless the race really happens, so it fails rather than reporting a green.");
    }
}
