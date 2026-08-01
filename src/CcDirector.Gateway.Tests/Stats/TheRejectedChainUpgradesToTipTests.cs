using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// A DATABASE ALREADY AT THE REJECTED ROUND-THREE STATE UPGRADES CLEANLY TO THE TIP CHAIN.
///
/// WHY THIS EXISTS. The tenant guard was rewritten three times, and twice a superseded migration was
/// COLLAPSED out of the chain rather than left in it. Collapsing is safe here for one reason and one
/// reason only - those migrations have never existed anywhere but an unmerged branch, so no database can
/// carry them in its history. That argument is about REACHABILITY, and it is not a substitute for showing
/// the transition works.
///
/// One database CAN be at the rejected state: a rig that a previous run of this branch migrated. That is
/// the last unproven edge on this work, and this fact closes it. Round three proved the analogous case for
/// an earlier collapse; nobody has proved this one.
///
/// WHAT "THE REJECTED STATE" IS, AND HOW IT IS BUILT. Faithfully, and by reconstruction rather than by
/// running the old assembly, which no longer exists in this build:
///
///   - the schema carries the round-three predicate, <c>"tenant" ~ '[^[:space:]]'</c>, under the same
///     constraint NAMES the tip uses, and
///   - the migration history names <c>20260801200050_TenantMustContainANonWhitespaceCharacter</c>, a
///     migration id the tip chain no longer contains.
///
/// Those two facts ARE the state - they are what a database migrated by the rejected chain would hold, and
/// they are what the upgrade has to cope with. The second is the interesting one: the repository's
/// migrations README warns specifically that a history row naming an id the chain no longer has can make
/// Entity Framework treat a migration as pending and re-run its <c>Up()</c> against a schema that already
/// exists. That is exactly the shape being tested.
/// </summary>
public sealed class TheRejectedChainUpgradesToTipTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>The leaf migration of the REJECTED round-three chain, recovered from git history at
    /// c5089c5f7. The tip chain does not contain this id.</summary>
    private const string RejectedLeafMigrationId = "20260801200050_TenantMustContainANonWhitespaceCharacter";

    /// <summary>The migration immediately BEFORE the rejected leaf, and the last one the rejected chain
    /// shares with tip. Migrating to this target reproduces the real pre-rejected schema, names and all.</summary>
    private const string MigrationBeforeTheRejectedLeaf = "20260801170503_TenantRequiredNoDefaultAndNonEmpty";

    /// <summary>The round-three predicate, verbatim. It accepts U+00A0, which is the defect.</summary>
    private const string RejectedPredicate = "\"tenant\" ~ '[^[:space:]]'";

    private static bool RigIsAbsent => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (RigIsAbsent)
                Skip = $"Set {ConnectionEnvVar} (scripts\\pg-stats-proof-rig.ps1 -Verb up) to prove the " +
                       "rejected chain upgrades to tip.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    private readonly ITestOutputHelper _out;

    public TheRejectedChainUpgradesToTipTests(ITestOutputHelper output) => _out = output;

    private static DbContextOptions<GatewayStatsDbContext> Options() =>
        new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            })
            .Options;

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        return connection;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [RequiresPostgresStatsFact]
    public void A_database_at_the_rejected_round_three_state_upgrades_to_tip_and_gains_the_allowlist()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to rebuild the schema in '{database}'.");

        var schema = GatewayStatsDbContext.PostgresSchema;

        // ---- 1. Build the rejected state by migrating TO THE MIGRATION BEFORE IT, then applying the
        //         rejected predicate over whatever that produced.
        //
        // Migrating to a NAMED TARGET rather than applying the whole chain and winding back is what makes
        // this faithful. An earlier version of this fact applied the tip chain and then re-created the
        // constraints from a computed name, which invented a state no database has been in - the tip
        // migration renames three concurrency constraints, so after a full apply the OLD names are simply
        // gone and cannot be read back out of the catalog. Stopping at the previous migration produces the
        // real pre-rejected schema, names and all, with nothing guessed.
        using (var ctx = new GatewayStatsDbContext(Options()))
        {
            // The const, not the local, so the interpolation is a compile-time constant and the
            // SQL-injection analyzer is satisfied by construction rather than suppressed.
            ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
            ctx.GetService<IMigrator>().Migrate(MigrationBeforeTheRejectedLeaf);
        }

        using var connection = Open();

        // Every guard, read from the catalog as (table, CONSTRAINT NAME) pairs rather than from a list here.
        //
        // THE NAME MUST BE PRESERVED, not recomputed, and getting that wrong is what this fact caught first
        // time out. Recomputing it from TenantNotEmptyConstraint() re-created the concurrency constraints
        // under their CORRECTED names, so the wind-back produced a state no database has ever been in and
        // the upgrade then failed trying to drop a name that was not there. A reconstruction that
        // "improves" the state it is reconstructing is not a reconstruction.
        var guards = new List<(string Table, string Constraint)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT c.relname, k.conname FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE n.nspname = '{schema}' AND k.conname LIKE 'ck_%_tenant_not_empty'";
            using var reader = read.ExecuteReader();
            while (reader.Read()) guards.Add((reader.GetString(0), reader.GetString(1)));
        }
        Assert.NotEmpty(guards);
        _out.WriteLine($"winding {guards.Count} guards back to the rejected round-three predicate, names preserved");

        foreach (var (table, name) in guards)
        {
            Execute(connection, $"ALTER TABLE {schema}.\"{table}\" DROP CONSTRAINT \"{name}\"");
            Execute(connection, $"ALTER TABLE {schema}.\"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({RejectedPredicate})");
        }

        // The history now names the REJECTED leaf, an id the tip chain does not contain, and no longer
        // names the tip leaf - which is precisely what a database migrated by that chain would hold.
        Execute(connection,
            $"DELETE FROM {schema}.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%TenantIsAnAllowlistedSpelling'");
        Execute(connection,
            $"INSERT INTO {schema}.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
            $"VALUES ('{RejectedLeafMigrationId}', '10.0.0')");

        // ---- 2. THE STATE IS REAL: the rejected predicate really does accept the character that defeated
        //         it. Without this the upgrade below could be fixing a state that was never broken.
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (chr(160), '2026-08-01T12', 's-nbsp', 'typed', 'desktop', false, 1, false, 1, 1)";
            probe.ExecuteNonQuery();
        }
        _out.WriteLine("confirmed: at the rejected state, U+00A0 is accepted as a tenant");

        // The row has to go before the upgrade - the tip constraint is validated against existing rows, so
        // leaving it would make the migration fail for the right reason at the wrong moment, and this fact
        // is about the TRANSITION, not about what to do with already-corrupt data.
        Execute(connection, $"DELETE FROM {schema}.stat_delta WHERE session_id = 's-nbsp'");

        // ---- 3. THE UPGRADE. This is the whole test.
        using (var ctx = new GatewayStatsDbContext(Options()))
        {
            var pending = ctx.Database.GetPendingMigrations().ToList();
            _out.WriteLine("pending at the rejected state: " + string.Join(", ", pending));
            ctx.Database.Migrate();
        }

        // ---- 4. THE RESULT: the allowlist is in force, and the row that defeated round three is refused.
        using (var refused = connection.CreateCommand())
        {
            refused.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (chr(160), '2026-08-01T12', 's-nbsp-2', 'typed', 'desktop', false, 1, false, 1, 1)";
            var thrown = Assert.ThrowsAny<PostgresException>(() => refused.ExecuteNonQuery());
            Assert.Equal("23514", thrown.SqlState);
            _out.WriteLine($"after the upgrade, U+00A0 is refused: {thrown.ConstraintName}");
        }

        // And a legitimate tenant still stores, so the upgrade did not simply seize the table shut.
        using (var accepted = connection.CreateCommand())
        {
            accepted.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES ('local', '2026-08-01T12', 's-after-upgrade', 'typed', 'desktop', false, 1, false, 2, 20)";
            Assert.Equal(1, accepted.ExecuteNonQuery());
        }
    }
}
