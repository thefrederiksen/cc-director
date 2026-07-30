using CcDirector.Gateway.Tests.Data.StatsSchemaProof;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The half of Step 2's privilege question that cannot be read off the hosted database: that a role
/// holding ONLY CREATE on the database - no superuser, no ownership of any existing schema - can in
/// fact create <c>gateway_stats</c>, create tables in it, and run an Entity Framework migration chain
/// there with its own <c>gateway_stats.__EFMigrationsHistory</c> table.
///
/// WHY THIS IS NOT ASKED OF THE HOSTED DATABASE. Staging shares production's database, so a failed
/// creation experiment would land on the live database the whole fleet depends on. The hosted role's
/// grants were therefore measured READ-ONLY, and the creation is proved here, against a local
/// throwaway container whose role mirrors those measured grants:
///
///     scripts\pg-stats-proof-rig.ps1 -Instance w1 -Port 55433 -Verb up
///
/// WHY THE MIRROR IS ASSERTED AND NOT ASSUMED. A local role that could do something gateway_app
/// cannot would produce a green that proves nothing about the hosted database - the most expensive
/// kind of result, because it ships as a proof. <see cref="RestrictedRole_MirrorsTheMeasuredHostedGrants"/>
/// reads the mirror back out of the catalog, so a drifted or over-privileged rig fails loud here
/// instead of quietly validating the wrong thing.
///
/// WHY THE SCHEMA IS DROPPED BEFORE EVERY CREATE. <c>migrationBuilder.EnsureSchema</c> emits
/// <c>CREATE SCHEMA IF NOT EXISTS</c>, which is a silent no-op when the schema is already there - so a
/// migrate run against an existing <c>gateway_stats</c> would pass identically whether the privilege
/// was present or absent. That is a guard supplying its own evidence. Every test here starts by
/// dropping the schema and asserting it is gone, so the creation that follows is a real one.
///
/// GATING. The class needs BOTH environment variables and reports SKIPPED unless both are set, so the
/// ordinary SQLite test run touches no server:
///
///     CC_GATEWAY_TEST_PG_CONNECTION        superuser - used ONLY to revoke and restore the grant
///     CC_GATEWAY_TEST_PG_STATS_CONNECTION  the restricted role - the subject of every proof
/// </summary>
public sealed class GatewayStatsSchemaPrivilegeProofTests
{
    private const string SuperuserEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string RestrictedEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>The schema the hosted statistics store will own, and the whole subject of this class.</summary>
    private const string StatsSchema = StatsSchemaProofDbContext.SchemaName;

    /// <summary>A Fact that skips itself unless BOTH rig connection strings are present. Setting Skip in
    /// the attribute reports the test as skipped rather than passed, so an unset variable can never be
    /// mistaken for a proof that ran.</summary>
    private sealed class RequiresRestrictedPostgresFactAttribute : FactAttribute
    {
        public RequiresRestrictedPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SuperuserEnvVar))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RestrictedEnvVar)))
            {
                Skip = $"Set {SuperuserEnvVar} and {RestrictedEnvVar} to run the restricted-role schema-creation proof. " +
                       @"Stand the rig up with: powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance <name> -Port <port> -Verb up";
            }
        }
    }

    private static string RestrictedConnection =>
        Environment.GetEnvironmentVariable(RestrictedEnvVar)
        ?? throw new InvalidOperationException($"{RestrictedEnvVar} is not set.");

    private static string SuperuserConnection =>
        Environment.GetEnvironmentVariable(SuperuserEnvVar)
        ?? throw new InvalidOperationException($"{SuperuserEnvVar} is not set.");

    /// <summary>
    /// The superuser credentials pointed at the RESTRICTED role's database. The rig deliberately gives
    /// the superuser its own database (so the older proof suite's EnsureDeleted cannot drop this one
    /// mid-run), but a grant on a database has to be issued from a connection to the same server, and
    /// naming the database explicitly keeps the revoke aimed at the database under test.
    /// </summary>
    private static string AdminConnectionToStatsDatabase()
    {
        var admin = new NpgsqlConnectionStringBuilder(SuperuserConnection)
        {
            Database = new NpgsqlConnectionStringBuilder(RestrictedConnection).Database,
        };
        return admin.ConnectionString;
    }

    /// <summary>
    /// Refuse to run against anything that is not the local throwaway rig. Two separate failure modes
    /// are being shut out. The two connection strings must address the SAME server, or the revoke would
    /// be issued against a database other than the one the proof then tests - and a revoke that lands
    /// nowhere makes the deliberate red pass for the wrong reason. And the target database name must
    /// carry the throwaway prefix, because this class drops schemas and revokes privileges: pointed at
    /// a real database it would do real damage. The two roles must also differ, or the "restricted"
    /// leg would be the superuser and every privilege assertion below would be theatre.
    /// </summary>
    private static void AssertRigShape()
    {
        var restricted = new NpgsqlConnectionStringBuilder(RestrictedConnection);
        var superuser = new NpgsqlConnectionStringBuilder(SuperuserConnection);

        if (!string.Equals(restricted.Host, superuser.Host, StringComparison.OrdinalIgnoreCase)
            || restricted.Port != superuser.Port)
        {
            throw new InvalidOperationException(
                $"{SuperuserEnvVar} ({superuser.Host}:{superuser.Port}) and {RestrictedEnvVar} " +
                $"({restricted.Host}:{restricted.Port}) must address the same server. Set both from one run of " +
                @"scripts\pg-stats-proof-rig.ps1 -Verb print-env.");
        }

        var database = restricted.Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to drop schemas and revoke privileges in database '{database}': its name must begin with " +
                $"the throwaway prefix 'ccpg'. Point {RestrictedEnvVar} at a disposable rig database.");
        }

        if (string.Equals(restricted.Username, superuser.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{RestrictedEnvVar} and {SuperuserEnvVar} name the same role ('{restricted.Username}'). The proof " +
                "needs a genuinely restricted role; running both legs as the superuser would prove nothing.");
        }
    }

    /// <summary>Build the proof context on the RESTRICTED role, with its migration chain in this test
    /// assembly and its history table inside gateway_stats - the wiring the hosted statistics context
    /// will use.</summary>
    private static StatsSchemaProofDbContext NewRestrictedContext()
    {
        var options = new DbContextOptionsBuilder<StatsSchemaProofDbContext>()
            .UseNpgsql(RestrictedConnection, npg =>
            {
                npg.MigrationsAssembly(typeof(StatsSchemaProofDbContext).Assembly.GetName().Name);
                npg.MigrationsHistoryTable(StatsSchemaProofDbContext.HistoryTableName, StatsSchema);
            })
            .Options;
        return new StatsSchemaProofDbContext(options);
    }

    // ---------------------------------------------------------------------------------------------
    // 1. The mirror. Everything below is only worth reading if this passes.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The local role holds exactly the grants the hosted gateway_app role was measured to hold, and no
    /// others. Each assertion below is one line of that measurement read back out of the catalog: not
    /// superuser, no CREATEDB, no CREATEROLE, no BYPASSRLS, inherits, member of no other role, not the
    /// database owner, CREATE on the database, USAGE but NOT CREATE on public, CREATE and USAGE on the
    /// pre-existing gateway schema.
    ///
    /// The database-owner check earns its place: an owner can create a schema by virtue of ownership
    /// even with no explicit grant, so a rig that accidentally made the role the owner would prove the
    /// creation works for a reason the hosted role cannot rely on.
    /// </summary>
    [RequiresRestrictedPostgresFact]
    public void RestrictedRole_MirrorsTheMeasuredHostedGrants()
    {
        AssertRigShape();
        using var conn = OpenRestricted();

        Assert.False(ScalarBool(conn, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user"),
            "The proof role must NOT be superuser - the hosted gateway_app is not.");
        Assert.False(ScalarBool(conn, "SELECT rolcreatedb FROM pg_roles WHERE rolname = current_user"),
            "The proof role must NOT have CREATEDB - the hosted gateway_app does not.");
        Assert.False(ScalarBool(conn, "SELECT rolcreaterole FROM pg_roles WHERE rolname = current_user"),
            "The proof role must NOT have CREATEROLE - the hosted gateway_app does not.");
        Assert.False(ScalarBool(conn, "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user"),
            "The proof role must NOT have BYPASSRLS - the hosted gateway_app does not.");
        Assert.True(ScalarBool(conn, "SELECT rolinherit FROM pg_roles WHERE rolname = current_user"),
            "The hosted gateway_app inherits; the proof role must too.");

        Assert.Equal(0, ScalarInt(conn,
            "SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member " +
            "WHERE r.rolname = current_user"));

        Assert.False(ScalarBool(conn,
            "SELECT d.datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user) " +
            "FROM pg_database d WHERE d.datname = current_database()"),
            "The proof role must NOT own the database - ownership would grant schema creation for a " +
            "reason the hosted role does not have.");

        // The privilege the entire Step 2 design rests on.
        Assert.True(ScalarBool(conn,
            "SELECT has_database_privilege(current_user, current_database(), 'CREATE')"));

        Assert.False(ScalarBool(conn, "SELECT has_schema_privilege(current_user, 'public', 'CREATE')"),
            "The hosted gateway_app cannot create in public; a local role that can would invalidate the proof.");
        Assert.True(ScalarBool(conn, "SELECT has_schema_privilege(current_user, 'public', 'USAGE')"));

        Assert.True(ScalarBool(conn, "SELECT has_schema_privilege(current_user, 'gateway', 'CREATE')"));
        Assert.True(ScalarBool(conn, "SELECT has_schema_privilege(current_user, 'gateway', 'USAGE')"));
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Raw SQL: the schema and a table in it.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// From nothing, the restricted role creates gateway_stats, owns it, creates a table inside it and
    /// round-trips a row. The schema is dropped and its absence asserted first, so the CREATE SCHEMA
    /// that follows cannot be a no-op over a schema somebody else made.
    ///
    /// The last assertion is the negative that keeps the whole rig honest: the same role, in the same
    /// session, is REFUSED a table in public. A role that could create anywhere would make the
    /// gateway_stats success unremarkable and untransferable to the hosted database.
    /// </summary>
    [RequiresRestrictedPostgresFact]
    public void RestrictedRole_CreatesStatsSchemaAndTable_FromNothing()
    {
        AssertRigShape();
        using var conn = OpenRestricted();

        DropStatsSchema(conn);
        Assert.Equal(0, ScalarInt(conn,
            $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));

        // No IF NOT EXISTS: this statement either creates the schema or fails.
        Execute(conn, $"CREATE SCHEMA {StatsSchema}");

        Assert.Equal(1, ScalarInt(conn,
            $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));
        Assert.Equal(
            ScalarString(conn, "SELECT current_user"),
            ScalarString(conn, $"SELECT schema_owner FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));

        Execute(conn, $"CREATE TABLE {StatsSchema}.privilege_probe (id bigint PRIMARY KEY, note text NOT NULL)");
        Execute(conn, $"INSERT INTO {StatsSchema}.privilege_probe (id, note) VALUES (1, 'created by the restricted role')");

        Assert.Equal("created by the restricted role",
            ScalarString(conn, $"SELECT note FROM {StatsSchema}.privilege_probe WHERE id = 1"));

        Assert.Equal(1, ScalarInt(conn,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{StatsSchema}' AND table_name = 'privilege_probe'"));
        Assert.Equal(0, ScalarInt(conn,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'privilege_probe'"));

        // The fence: the same role cannot create in public. 42501 is insufficient_privilege.
        var refused = Assert.Throws<PostgresException>(() =>
            Execute(conn, "CREATE TABLE public.privilege_probe (id bigint PRIMARY KEY)"));
        Assert.Equal("42501", refused.SqlState);
        Assert.Contains("permission denied for schema public", refused.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Entity Framework: the migration chain, with its history table inside the new schema.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The restricted role runs a two-migration Entity Framework chain from nothing into gateway_stats.
    /// Both migrations must land, which is the point of a chain rather than a single migration: after
    /// the first one, Entity Framework has to read its history table back OUT of gateway_stats to work
    /// out what to apply next, and a history table written to the wrong schema shows up exactly there.
    /// The second migration also ALTERs a table already inside the schema, not only creates fresh ones.
    ///
    /// Every object is asserted present in gateway_stats and absent from public, because the hosted
    /// role cannot create in public - a model that leaked one object there would deploy red.
    /// </summary>
    [RequiresRestrictedPostgresFact]
    public void RestrictedRole_AppliesMigrationChain_WithHistoryTableInsideStatsSchema()
    {
        AssertRigShape();
        using (var conn = OpenRestricted())
        {
            DropStatsSchema(conn);
            Assert.Equal(0, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));
        }

        using (var ctx = NewRestrictedContext())
        {
            ctx.Database.Migrate();

            var defined = ctx.Database.GetMigrations().ToList();
            var applied = ctx.Database.GetAppliedMigrations().ToList();
            Assert.Equal(2, defined.Count);
            Assert.Equal(defined, applied);
        }

        using (var conn = OpenRestricted())
        {
            Assert.Equal(
                ScalarString(conn, "SELECT current_user"),
                ScalarString(conn, $"SELECT schema_owner FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));

            // The history table is inside gateway_stats, holds both migrations, and is NOT in public.
            Assert.Equal(1, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{StatsSchema}' " +
                $"AND table_name = '{StatsSchemaProofDbContext.HistoryTableName}'"));
            Assert.Equal(0, ScalarInt(conn,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' " +
                $"AND table_name = '{StatsSchemaProofDbContext.HistoryTableName}'"));
            Assert.Equal(2, ScalarInt(conn,
                $"SELECT count(*) FROM {StatsSchema}.\"{StatsSchemaProofDbContext.HistoryTableName}\""));

            foreach (var table in new[] { "proof_delta", "proof_highwater", "proof_meta" })
            {
                Assert.Equal(1, ScalarInt(conn,
                    $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{StatsSchema}' AND table_name = '{table}'"));
                Assert.Equal(0, ScalarInt(conn,
                    $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
            }

            // The second migration's ALTER really ran: proof_delta carries the column it added.
            Assert.Equal(1, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.columns WHERE table_schema = '{StatsSchema}' " +
                "AND table_name = 'proof_delta' AND column_name = 'chars'"));
        }

        // And the schema is usable through Entity Framework, not merely present: write in one context,
        // read in another.
        var sessionId = Guid.NewGuid().ToString();
        using (var ctx = NewRestrictedContext())
        {
            ctx.ProofDeltas.Add(new ProofDeltaRow { HourUtc = "2026-07-30T16", Tenant = sessionId, Turns = 3, Chars = 42 });
            ctx.ProofHighwater.Add(new ProofHighwaterRow { Tenant = sessionId, SessionId = sessionId, Turns = 3 });
            ctx.ProofMeta.Add(new ProofMetaRow { Tenant = sessionId, Name = "agents_since_utc", Value = "2026-07-30T16" });
            ctx.SaveChanges();
        }

        using (var ctx = NewRestrictedContext())
        {
            var delta = ctx.ProofDeltas.Single(d => d.Tenant == sessionId);
            Assert.Equal("2026-07-30T16", delta.HourUtc);
            Assert.Equal(3, delta.Turns);
            Assert.Equal(42, delta.Chars);
            Assert.True(delta.Id > 0, "The generated key must come back populated.");

            Assert.Equal(3, ctx.ProofHighwater.Single(h => h.Tenant == sessionId).Turns);
            Assert.Equal("2026-07-30T16", ctx.ProofMeta.Single(m => m.Tenant == sessionId).Value);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 4. The failing direction, made permanent.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Take the one privilege away and both the raw CREATE SCHEMA and the Entity Framework migrate must
    /// go red, with an error naming insufficient privilege on the database - then give it back and
    /// watch the same migrate go green.
    ///
    /// This is what makes the class a proof rather than a demonstration. Without it, every assertion
    /// above would pass just as happily if CREATE on the database were irrelevant to the outcome - and
    /// the whole Step 2 design rests on the claim that it is the privilege that matters. Pinning the
    /// SQLSTATE and the message text is deliberate too: a test that only asserted "something threw"
    /// would stay green if the failure moved to a connection error, a missing table, or a typo in the
    /// schema name, and would then be reporting a privilege result it never observed.
    ///
    /// The grant is restored in a finally block. If a failure left it revoked, every later run would
    /// fail for a reason unrelated to what it measures.
    /// </summary>
    [RequiresRestrictedPostgresFact]
    public void RestrictedRole_WithoutDatabaseCreate_CannotCreateStatsSchema_AndCanAgainOnceRestored()
    {
        AssertRigShape();

        var restricted = new NpgsqlConnectionStringBuilder(RestrictedConnection);
        var database = QuoteIdentifier(restricted.Database ?? throw new InvalidOperationException("The restricted connection string names no database."));
        var role = QuoteIdentifier(restricted.Username ?? throw new InvalidOperationException("The restricted connection string names no user."));

        using (var conn = OpenRestricted())
        {
            DropStatsSchema(conn);
            Assert.Equal(0, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));
        }

        try
        {
            using (var admin = OpenAdmin())
            {
                Execute(admin, $"REVOKE CREATE ON DATABASE {database} FROM {role}");
            }

            using (var conn = OpenRestricted())
            {
                Assert.False(ScalarBool(conn,
                    "SELECT has_database_privilege(current_user, current_database(), 'CREATE')"),
                    "The revoke must have landed, or the red below would be measuring nothing.");

                var refused = Assert.Throws<PostgresException>(() => Execute(conn, $"CREATE SCHEMA {StatsSchema}"));
                Assert.Equal("42501", refused.SqlState);
                Assert.Contains("permission denied for database", refused.MessageText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(restricted.Database!, refused.MessageText, StringComparison.OrdinalIgnoreCase);
            }

            // The same refusal reaches Entity Framework: the migrate cannot create its schema either.
            using (var ctx = NewRestrictedContext())
            {
                var thrown = Assert.ThrowsAny<Exception>(() => ctx.Database.Migrate());
                var postgres = FindPostgresException(thrown);
                Assert.NotNull(postgres);
                Assert.Equal("42501", postgres!.SqlState);
                Assert.Contains("permission denied for database", postgres.MessageText, StringComparison.OrdinalIgnoreCase);
            }

            using (var conn = OpenRestricted())
            {
                Assert.Equal(0, ScalarInt(conn,
                    $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));
            }
        }
        finally
        {
            using var admin = OpenAdmin();
            Execute(admin, $"GRANT CREATE ON DATABASE {database} TO {role}");
        }

        // Restored: the identical migrate now succeeds. Same code, same connection, one privilege.
        using (var conn = OpenRestricted())
        {
            Assert.True(ScalarBool(conn,
                "SELECT has_database_privilege(current_user, current_database(), 'CREATE')"));
        }

        using (var ctx = NewRestrictedContext())
        {
            ctx.Database.Migrate();
            Assert.Equal(ctx.Database.GetMigrations(), ctx.Database.GetAppliedMigrations());
        }

        using (var conn = OpenRestricted())
        {
            Assert.Equal(1, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{StatsSchema}'"));
            Assert.Equal(1, ScalarInt(conn,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{StatsSchema}' " +
                $"AND table_name = '{StatsSchemaProofDbContext.HistoryTableName}'"));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    private static NpgsqlConnection OpenRestricted()
    {
        var conn = new NpgsqlConnection(RestrictedConnection);
        conn.Open();
        return conn;
    }

    private static NpgsqlConnection OpenAdmin()
    {
        var conn = new NpgsqlConnection(AdminConnectionToStatsDatabase());
        conn.Open();
        return conn;
    }

    /// <summary>Drop the statistics schema as the restricted role, so the next create is a real one.
    /// The role owns the schema whenever it exists here, which is itself part of what is being proved.</summary>
    private static void DropStatsSchema(NpgsqlConnection conn) =>
        Execute(conn, $"DROP SCHEMA IF EXISTS {StatsSchema} CASCADE");

    /// <summary>Entity Framework wraps provider failures, so the privilege assertion has to reach the
    /// PostgresException that actually carries the SQLSTATE rather than assert on the wrapper.</summary>
    private static PostgresException? FindPostgresException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException postgres)
                return postgres;
            exception = exception.InnerException;
        }
        return null;
    }

    /// <summary>Quote an identifier read out of a connection string, so a name needing quotes (or
    /// carrying one) cannot change the shape of the statement it lands in.</summary>
    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static void Execute(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static bool ScalarBool(NpgsqlConnection conn, string sql) => Convert.ToBoolean(Scalar(conn, sql));

    private static int ScalarInt(NpgsqlConnection conn, string sql) => Convert.ToInt32(Scalar(conn, sql));

    private static string ScalarString(NpgsqlConnection conn, string sql) => Convert.ToString(Scalar(conn, sql)) ?? "";

    private static object Scalar(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        return cmd.ExecuteScalar()
            ?? throw new InvalidOperationException($"Expected a value from: {sql}");
    }
}
