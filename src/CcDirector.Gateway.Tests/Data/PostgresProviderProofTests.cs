using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Real-PostgreSQL proof for the Hosted Gateway data layer (Step 4a): the SAME provider-agnostic
/// <see cref="GatewayDbContext"/> model, run on an actual Postgres server, migrates cleanly and round-trips
/// the three shapes that could diverge between SQLite and Postgres - a JSON-owned column, a natural-key column
/// whose ordering depends on the collation, and a UTC timestamp.
///
/// The whole class is gated behind the <c>CC_GATEWAY_TEST_PG_CONNECTION</c> environment variable: with it
/// UNSET (the normal SQLite test run and CI) every fact here reports SKIPPED and nothing touches a database.
/// Set it to a Postgres connection string (a throwaway container) to run the proof. The migration assembly is
/// <c>CcDirector.Gateway.Migrations.Postgres</c>, referenced by this test project so it loads at runtime, and
/// the migrations history table lives in the <c>gateway</c> schema - exactly as the runtime Gateway wires it.
/// </summary>
public sealed class PostgresProviderProofTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";

    /// <summary>A Fact that skips itself when <see cref="ConnectionEnvVar"/> is unset, so the default test run
    /// (SQLite, no Postgres server) is unaffected. Setting Skip in the attribute reports the test as skipped
    /// rather than passed.</summary>
    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres proof.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    /// <summary>Build a GatewayDbContext on Npgsql pointing at the proof container, with the Postgres migration
    /// assembly and the gateway-schema history table - the same wiring the runtime Gateway uses.</summary>
    private static GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    /// <summary>
    /// Applies the Postgres migration set on a clean database and asserts the gateway schema and the tables
    /// were created. Dropping first (EnsureDeleted) makes the migrate a genuine from-nothing run so the schema
    /// creation is really exercised, not just found already present.
    /// </summary>
    /// <summary>The 22 mapped tables, all expected under the gateway schema and NONE under public.</summary>
    private static readonly string[] MappedTables =
    {
        "cron_jobs", "cron_runs", "worklists", "worklist_items", "workflows", "workflow_versions",
        "workflow_files", "workflow_runs", "snoozes", "governance_events", "push_subscriptions",
        "wingman_instructions", "session_spend", "account_hosted_ai_spend", "mission_notes",
        "governance_audit_events", "device_credentials", "device_import_markers",
        "dictation_transcripts", "tenant_settings", "workflow_tenant_overrides", "activity_events",
    };

    [RequiresPostgresFact]
    public void Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres()
    {
        using var ctx = NewContext();
        GuardThrowawayDatabase();
        ctx.Database.EnsureDeleted();
        ctx.Database.Migrate();

        // The gateway schema exists.
        Assert.Equal(1, ScalarInt(ctx,
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'gateway'"));

        // Every mapped table landed under the gateway schema - and NONE of them under public.
        foreach (var table in MappedTables)
        {
            Assert.Equal(1, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = '{table}'"));
            Assert.Equal(0, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
        }

        // The migrations history table lives in the gateway schema too - not in public.
        Assert.Equal(1, ScalarInt(ctx,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = '__EFMigrationsHistory'"));
        Assert.Equal(0, ScalarInt(ctx,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'"));
    }

    /// <summary>
    /// Proves the migration applied an EXPLICIT byte-ordinal collation "C" to exactly the four natural-key
    /// primary-key columns and to no others. This reads pg_attribute.attcollation (the column's DEFINED
    /// collation), which is the type-default pseudo-collation ("default") for a plain text column and the
    /// "C" collation only where the migration set COLLATE "C" explicitly - so it distinguishes our explicit
    /// collation from the database's default collation even if that default happens to be C. Without this,
    /// the behavioral ordering test alone could pass on a container whose default collation is already C.
    /// </summary>
    [RequiresPostgresFact]
    public void Collation_ExplicitC_OnExactlyTheFourNaturalKeys_OnRealPostgres()
    {
        EnsureMigrated();
        using var ctx = NewContext();

        // The DEFINED collation per column, joined through pg_collation, across the gateway schema. A plain
        // text column reads back "default"; only an explicit COLLATE "C" reads back "C".
        var withExplicitC = QueryPairs(ctx,
            "SELECT c.relname, a.attname " +
            "FROM pg_attribute a " +
            "JOIN pg_class c ON c.oid = a.attrelid " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_collation col ON col.oid = a.attcollation " +
            "WHERE n.nspname = 'gateway' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped " +
            "AND col.collname = 'C' " +
            "ORDER BY c.relname, a.attname");

        var expected = new[]
        {
            // The device-credential natural keys (MTR-14): the DeviceId primary key and the DeviceKeyHash lookup
            // index, plus the device-import marker's SourcePath key - all byte-ordinal to match SQLite BINARY.
            ("device_credentials", "DeviceId"),
            ("device_credentials", "DeviceKeyHash"),
            ("device_import_markers", "SourcePath"),
            ("mission_notes", "Key"),
            ("push_subscriptions", "Endpoint"),
            ("session_spend", "SessionId"),
            ("snoozes", "SessionId"),
            // The per-account settings key (AddTenantSettings): tenant_settings.Key is byte-ordinal
            // (GatewayDbContext UseCollation("C")) so a settings key compares exactly. This entry was
            // missing here because the PG-gated suite never runs in CI; real-Postgres proof surfaced the gap.
            ("tenant_settings", "Key"),
            ("tenants", "AccountSubject"),
            ("tenants", "Id"),
        };
        Assert.Equal(expected, withExplicitC.ToArray());

        // And no gateway column carries any explicit collation OTHER than the default or "C" - nothing
        // unintended slipped in.
        Assert.Equal(0, ScalarInt(ctx,
            "SELECT count(*) " +
            "FROM pg_attribute a " +
            "JOIN pg_class c ON c.oid = a.attrelid " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_collation col ON col.oid = a.attcollation " +
            "WHERE n.nspname = 'gateway' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped " +
            "AND col.collname NOT IN ('default', 'C')"));
    }

    /// <summary>
    /// A JSON-owned-column store: a WorkflowVersion carries Steps and OutcomeCriteria as owned collections
    /// serialized into jsonb columns. Writing then reading in a fresh context proves the owned sub-documents
    /// round-trip field-for-field through Postgres jsonb.
    /// </summary>
    [RequiresPostgresFact]
    public void WorkflowVersion_JsonOwnedColumns_RoundTrip_OnRealPostgres()
    {
        EnsureMigrated();

        // The key is minted by GatewayMintedKeyEntity, not chosen here - read it back off the row we added.
        Guid id;
        using (var ctx = NewContext())
        {
            var row = new WorkflowVersionEntity
            {
                TenantId = TenantId.Local.Value,
                WorkflowId = "wf-json-proof",
                Version = 1,
                Status = WorkflowVersionStatus.Published,
                Name = "JSON proof",
                Summary = "owned collections to jsonb",
                Steps = new List<WorkflowStepDto>
                {
                    new() { Name = "Plan", Description = "d1", Doer = "architect", Reviewer = null, Done = "planned" },
                    new() { Name = "Build", Description = "d2", Doer = "worker", Reviewer = "qa", Done = "merged" },
                },
                OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>
                {
                    new() { CriterionId = "merged-pr", Description = "the PR is merged", ProofHint = "the merged pull request URL" },
                    new() { CriterionId = "green-ci", Description = "CI is green", ProofHint = null },
                },
                InstructionsMarkdown = "# conduct",
                ContentHash = "hash",
                AuthoredBy = "test",
                CreatedUtc = DateTime.UtcNow,
            };
            ctx.WorkflowVersions.Add(row);
            ctx.SaveChanges();
            id = row.Id;
        }

        using (var ctx = NewContext())
        {
            var read = ctx.WorkflowVersions.Single(v => v.Id == id);

            Assert.Equal(2, read.Steps.Count);
            Assert.Equal("Plan", read.Steps[0].Name);
            Assert.Null(read.Steps[0].Reviewer);
            Assert.Equal("Build", read.Steps[1].Name);
            Assert.Equal("qa", read.Steps[1].Reviewer);
            Assert.Equal("merged", read.Steps[1].Done);

            Assert.Equal(2, read.OutcomeCriteria.Count);
            Assert.Equal("merged-pr", read.OutcomeCriteria[0].CriterionId);
            Assert.Equal("the merged pull request URL", read.OutcomeCriteria[0].ProofHint);
            Assert.Equal("green-ci", read.OutcomeCriteria[1].CriterionId);
            Assert.Null(read.OutcomeCriteria[1].ProofHint);
        }
    }

    /// <summary>
    /// A natural-key/collation store: the push_subscriptions Endpoint primary key carries an explicit "C"
    /// (byte-ordinal) collation on Postgres, matching SQLite's default BINARY collation. Keys that differ by
    /// byte order (uppercase bytes sort before lowercase in "C", but a locale collation would interleave them)
    /// must come back in byte order, and the primary key must reject a duplicate.
    /// </summary>
    [RequiresPostgresFact]
    public void PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres()
    {
        EnsureMigrated();

        // Byte order: 'B'(66) < 'C'(67) < 'a'(97). A locale (en_US-style) collation would instead group case-
        // insensitively and put "endpoint_a" first, so asserting [B, C, a] distinguishes "C" from a locale one.
        var endpoints = new[] { "endpoint_a", "endpoint_C", "endpoint_B" };
        using (var ctx = NewContext())
        {
            foreach (var e in endpoints)
                ctx.PushSubscriptions.Add(new PushSubscriptionEntity
                {
                    Endpoint = e,
                    TenantId = TenantId.Local.Value,
                    P256dh = "p",
                    Auth = "a",
                    CreatedAtUtc = DateTime.UtcNow,
                });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var ordered = ctx.PushSubscriptions
                .Where(p => p.Endpoint.StartsWith("endpoint_"))
                .OrderBy(p => p.Endpoint)
                .Select(p => p.Endpoint)
                .ToList();
            Assert.Equal(new[] { "endpoint_B", "endpoint_C", "endpoint_a" }, ordered);
        }

        // Uniqueness on the natural key: a second row with an existing Endpoint is rejected.
        using (var ctx = NewContext())
        {
            ctx.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = "endpoint_B",
                TenantId = TenantId.Local.Value,
                P256dh = "p2",
                Auth = "a2",
                CreatedAtUtc = DateTime.UtcNow,
            });
            Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
        }
    }

    /// <summary>
    /// The session_spend store: a row with UTC timestamps written then read in a fresh context. The value must
    /// come back equal and with Kind == Utc, proving the UTC DateTime convention round-trips through Postgres
    /// (Npgsql maps a Kind=Utc DateTime to timestamp with time zone).
    /// </summary>
    [RequiresPostgresFact]
    public void SessionSpend_UtcTimestampRoundTrip_OnRealPostgres()
    {
        EnsureMigrated();

        var sessionId = Guid.NewGuid().ToString();
        var first = new DateTime(2026, 7, 18, 13, 45, 30, DateTimeKind.Utc);
        var last = new DateTime(2026, 7, 18, 14, 05, 00, DateTimeKind.Utc);

        using (var ctx = NewContext())
        {
            ctx.SessionSpend.Add(new SessionSpendEntity
            {
                SessionId = sessionId,
                TenantId = TenantId.Local.Value,
                AgentKind = "test-agent",
                TokensCaptured = true,
                InputTokens = 1000,
                OutputTokens = 200,
                BillingMode = "metered",
                MeteredCostMicros = 12345,
                FirstObservedUtc = first,
                LastObservedUtc = last,
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var read = ctx.SessionSpend.Single(s => s.SessionId == sessionId);
            Assert.Equal("test-agent", read.AgentKind);
            Assert.Equal(1000, read.InputTokens);
            Assert.Equal(12345, read.MeteredCostMicros);

            Assert.Equal(DateTimeKind.Utc, read.FirstObservedUtc.Kind);
            Assert.Equal(first, read.FirstObservedUtc);
            Assert.Equal(DateTimeKind.Utc, read.LastObservedUtc.Kind);
            Assert.Equal(last, read.LastObservedUtc);
        }
    }

    /// <summary>Ensure the schema is present before a round-trip test, without assuming test ordering.</summary>
    private static void EnsureMigrated()
    {
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Refuse to drop a database that is not obviously a throwaway. EnsureDeleted() would DROP whatever the
    /// connection points at, so before the from-nothing migrate test drops it, the target database NAME must
    /// begin with the dedicated throwaway prefix "ccpg" - a token no real database would carry. A loose
    /// substring marker like "test" is deliberately NOT used: it matches ordinary names such as "latest" or
    /// "contest", which would defeat the guard. Anything without the prefix throws instead of dropping, so
    /// pointing the env var at a real database can never nuke it.
    /// </summary>
    private static void GuardThrowawayDatabase()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to EnsureDeleted() the database '{database}': its name must begin with the throwaway " +
                "prefix 'ccpg'. Point CC_GATEWAY_TEST_PG_CONNECTION at a disposable database (e.g. 'ccpgproof').");
    }

    private static int ScalarInt(GatewayDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
            opened = true;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

    /// <summary>Run a two-column query and return the rows as (col0, col1) string pairs, in query order.</summary>
    private static List<(string, string)> QueryPairs(GatewayDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
            opened = true;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, string)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
