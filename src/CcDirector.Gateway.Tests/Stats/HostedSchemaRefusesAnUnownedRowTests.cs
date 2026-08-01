using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// THE HOSTED STATISTICS SCHEMA REFUSES A ROW THAT NAMES NO OWNER - both ways of failing to name one.
///
/// WHY THIS IS A SCHEMA RULE AND NOT A CODE REVIEW. Every write path in the statistics store passes a
/// tenant today; that was checked, and it is true. It is also not the point. On a shared hosted schema the
/// question is not "does the current code remember" but "what happens to the next writer that forgets" -
/// and the answer was: the row is accepted and silently filed under somebody. One customer's numbers land
/// in another customer's partition, quietly, with nothing to notice.
///
/// TWO HOLES, AND CLOSING ONE WOULD HAVE BEEN HALF A FIX:
///
///  1. THE OMITTED COLUMN. The hosted PostgreSQL tables were created with <c>DEFAULT 'local'</c>, carried
///     over from the SQLite schema where it is harmless because self-host has exactly one tenant. On the
///     multi-tenant schema it is fail-open: an INSERT that never mentions the tenant column gets Local.
///     Removed - on PostgreSQL only, because ripping it out of SQLite would force a table rebuild on every
///     statistics file already on disk to buy nothing.
///  2. THE EMPTY STRING. Dropping the default alone does not close it. The CLR property initialises to
///     <c>""</c>, so a write that forgets to set the tenant does not send NULL and hit the missing default -
///     it sends an empty string, which is a perfectly valid value naming nobody. A check constraint refuses
///     it.
///
/// Unattributed is unattributed either way, so the database refuses both rather than making one of them
/// merely harder.
///
/// Gated on <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> - the restricted-role rig connection - because the
/// constraint being proved is a PostgreSQL one and SQLite deliberately still has the default.
/// </summary>
public sealed class HostedSchemaRefusesAnUnownedRowTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} (scripts\\pg-stats-proof-rig.ps1 -Verb up) to prove the hosted " +
                       "schema refuses an unowned row.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    private readonly ITestOutputHelper _out;

    public HostedSchemaRefusesAnUnownedRowTests(ITestOutputHelper output)
    {
        _out = output;
        Reset();
    }

    /// <summary>Build the schema from the MIGRATION CHAIN, not from the model. The constraint being proved
    /// has to be one a real hosted deploy would get, and a hosted deploy gets whatever the migrations
    /// create - so a schema built from the model could pass here while the shipped chain never applied it.</summary>
    private static void Reset()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in '{database}': it must be a throwaway rig database " +
                "whose name begins with 'ccpg'.");

        // CONFIGURED THE WAY GatewayStatsStore CONFIGURES IT, and both lines are load-bearing. A plain
        // UseNpgsql leaves Entity Framework looking for migrations in the assembly that holds the context -
        // which is the SQLITE chain - so Migrate() runs the wrong chain against PostgreSQL and stops with
        // "the model has pending changes". That is what happened on the first run of this file, and the
        // failure was the test's, not the schema's.
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            })
            .Options;
        using var ctx = new GatewayStatsDbContext(options);
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
        ctx.Database.Migrate();
    }

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        return connection;
    }

    /// <summary>A row with every required column EXCEPT the tenant. Before this change the missing default
    /// filed it under Local; now there is no default and the column is NOT NULL, so the insert fails.</summary>
    [RequiresPostgresStatsFact]
    public void An_insert_that_omits_the_tenant_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('2026-08-01T12', 's-no-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"OMITTED TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        // 23502 is not_null_violation - the row was refused for the stated reason, not for an unrelated one
        // like a missing column, which would make this pass while proving nothing.
        Assert.Equal("23502", thrown.SqlState);
    }

    /// <summary>The hole dropping the default does NOT close: the tenant is present and empty, which is
    /// what a forgotten assignment through the CLR model actually sends.</summary>
    [RequiresPostgresStatsFact]
    public void An_insert_whose_tenant_is_empty_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('', '2026-08-01T12', 's-empty-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"EMPTY TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        // 23514 is check_violation, and the constraint is named, so this cannot pass on some other check.
        Assert.Equal("23514", thrown.SqlState);
        Assert.Equal(GatewayStatsDbContext.TenantNotEmptyConstraint("stat_delta"), thrown.ConstraintName);
    }

    /// <summary>
    /// THE THIRD SPELLING OF "NAMES NOBODY", and the one that got through.
    ///
    /// The first version of the constraint was <c>tenant &lt;&gt; ''</c>, which refuses the empty string and
    /// nothing else. An inspector stood up the restricted-role rig, applied this exact chain, inserted a
    /// tenant of THREE SPACES and read it back at length 3. That is not a hypothetical: whitespace was
    /// storable, and <see cref="TenantId"/> itself rejects a whitespace tenant, so the schema was enforcing
    /// a spelling of the invariant rather than the invariant.
    ///
    /// The two facts above tested only the two spellings that already worked, which is why the hole
    /// survived. This one exists because the fix is not "add btrim" - it is "assert the case that got
    /// through".
    /// </summary>
    [RequiresPostgresStatsFact]
    public void An_insert_whose_tenant_is_only_whitespace_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('   ', '2026-08-01T12', 's-whitespace-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"WHITESPACE TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        Assert.Equal("23514", thrown.SqlState);
        Assert.Equal(GatewayStatsDbContext.TenantNotEmptyConstraint("stat_delta"), thrown.ConstraintName);

        // And nothing was stored under it - the refusal is the whole point, not a warning.
        using var count = connection.CreateCommand();
        count.CommandText =
            $"SELECT COUNT(*) FROM {GatewayStatsDbContext.PostgresSchema}.stat_delta WHERE session_id = 's-whitespace-tenant'";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    /// <summary>Tab and newline are whitespace too, and a predicate that handled only the space character
    /// would pass the fact above while leaving the hole open one keystroke over.</summary>
    [RequiresPostgresStatsFact]
    public void An_insert_whose_tenant_is_a_tab_or_newline_is_refused()
    {
        foreach (var (spelling, label) in new[] { ("\t", "tab"), ("\n", "newline"), (" \t \n ", "mixed") })
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (@t, '2026-08-01T12', 's-ws', 'typed', 'desktop', false, 1, false, 1, 1)";
            cmd.Parameters.AddWithValue("t", spelling);

            var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
            _out.WriteLine($"{label} TENANT refused: {thrown.SqlState} {thrown.ConstraintName}");
            Assert.Equal("23514", thrown.SqlState);
        }
    }

    /// <summary>
    /// THE CONTROL. Without it every refusal above would pass against a table that refuses EVERY insert - a
    /// typo in the column list, a schema that never got created, a constraint that is simply wrong. A row
    /// that DOES name its owner must still be stored.
    /// </summary>
    [RequiresPostgresStatsFact]
    public void A_row_that_names_its_owner_is_still_stored()
    {
        using var connection = Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES ('tenant-alpha', '2026-08-01T12', 's-owned', 'typed', 'desktop', false, 1, false, 3, 30)";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        using var read = connection.CreateCommand();
        read.CommandText =
            $"SELECT turns FROM {GatewayStatsDbContext.PostgresSchema}.stat_delta WHERE session_id = 's-owned'";
        Assert.Equal(3L, Convert.ToInt64(read.ExecuteScalar()));
    }
}
