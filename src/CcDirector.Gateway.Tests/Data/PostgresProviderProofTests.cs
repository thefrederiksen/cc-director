using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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

    // Per RUN, not per operator: PostgresProofDatabase appends a unique suffix to the supplied
    // database name so two concurrent runs cannot EnsureDeleted() each other's schema (issue #1156).
    private static string Connection => PostgresProofDatabase.Connection;

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
    /// <summary>
    /// A FLOOR of known-good table names, NOT the definition of "every mapped table" - the exhaustive set
    /// is derived from the model in <see cref="MappedTablesFromModel"/>, because this array previously said
    /// "the 22 mapped tables" while the model mapped 38, and a sample that calls itself a census passes
    /// green precisely when a newly added table is the one in question.
    ///
    /// It is kept because it still earns its place: it asserts that these particular tables have not
    /// silently stopped being mapped at all, which a purely derived check cannot notice.
    /// </summary>
    private static readonly string[] MappedTables =
    {
        "cron_jobs", "cron_runs", "worklists", "worklist_items", "workflows", "workflow_versions",
        "workflow_files", "workflow_runs", "snoozes", "governance_events", "push_subscriptions",
        "wingman_instructions", "session_spend", "account_hosted_ai_spend", "mission_notes",
        "governance_audit_events", "device_credentials", "device_import_markers",
        "dictation_transcripts", "tenant_settings", "workflow_tenant_overrides", "activity_events",
        // The administrator trial-extension ledger. It matters that this one is asserted to land in
        // `gateway` and not in `public`: it is the audit trail of a write the WEBSITE's role was
        // deliberately never given, and a copy of it sitting in the website's own schema would put the
        // record of that decision on the wrong side of the boundary the whole design turns on.
        "trial_extensions",
    };

    /// <summary>
    /// EVERY mapped table, asked of the MODEL rather than of a list somebody maintains.
    ///
    /// The hand-written array above claimed to be "every mapped table" while naming 23 of the 38 the model
    /// actually maps. That is the failure mode this whole file exists to catch, committed by the file
    /// itself: a check whose claim is exhaustive and whose behaviour is a sample passes green while the
    /// table it should have caught is one of the fifteen nobody listed. A NEW table - which is exactly when
    /// "did it land in the right schema?" is a live question - was the case it could never answer.
    ///
    /// Asking the model instead makes the check exhaustive by construction: a table added tomorrow is
    /// checked tomorrow, with nobody remembering anything. Owned types are excluded because they are JSON
    /// sub-documents riding inside their owner's row, not tables.
    /// </summary>
    private static IReadOnlyList<string> MappedTablesFromModel(GatewayDbContext ctx) =>
        // THE DESIGN-TIME MODEL, not ctx.Model. The runtime model is read-optimized and has dropped the
        // migrations metadata, so asking it whether a table is excluded from migrations throws rather than
        // answering - which is the better failure of the two, but it is still the wrong model to ask.
        ctx.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            // EXCLUDED FROM MIGRATIONS MEANS WE DO NOT CREATE IT. `entitlements` is the payment side's
            // table, which this Gateway only READS - so a from-nothing migrate correctly leaves it absent,
            // and asserting it landed would fail on a truth rather than on a defect. Mapped is not the same
            // question as ours-to-create, and this check is about the second.
            .Where(e => !e.IsTableExcludedFromMigrations())
            .Select(e => e.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

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

        var mapped = MappedTablesFromModel(ctx);

        // The scan really found the model's tables - a reflection no-op returning nothing would otherwise
        // make the loop below vacuous and this test green while proving nothing at all.
        Assert.True(mapped.Count >= MappedTables.Length,
            $"the model mapped only {mapped.Count} tables, fewer than the {MappedTables.Length} known-good " +
            "names - the model scan is broken, not the schema");

        // The known-good names are a FLOOR, not the definition: if any of them ever stops being mapped that
        // is a real change to notice, and the derived set above is what makes the check exhaustive.
        foreach (var known in MappedTables)
            Assert.Contains(known, mapped, StringComparer.Ordinal);

        // Every mapped table landed under the gateway schema - and NONE of them under public.
        foreach (var table in mapped)
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
    /// Column names exempt from the byte-ordinal collation requirement WHEREVER they appear, with the
    /// reason. This and <see cref="CollationExceptions"/> are the only hand-maintained parts of the check,
    /// and they are the parts that have to be ARGUED FOR - not the part that decides what gets looked at.
    /// </summary>
    private static readonly HashSet<string> ExemptColumnNames = new(StringComparer.Ordinal)
    {
        // tenant_id leads almost every composite primary key in this schema, and the exemption rests on it
        // NOT being caller-supplied: every write stamps it from the RESOLVED tenant context, never from a
        // request body or a push payload, so no two spellings of one tenant id can reach the database and
        // there is nothing for the two providers to disagree about.
        //
        // THE PROOF THAT PREMISE RESTS ON, named here so the exemption is tied to it rather than merely
        // asserted: OmittedTenantBoundaryFailClosedTests. It replays thirteen surfaces - including
        // DirectorHub.Hello, which binds the very push stream the screen and turn stores are written
        // through - against an identical harness differing ONLY in the boundary argument, and proves each
        // resolver REFUSES rather than defaulting to the shared Local partition when no tenant resolves.
        //
        // WHAT THAT DOES NOT ESTABLISH, said rather than glossed: it proves the tenant is RESOLVED and
        // never defaulted. It does not enumerate every payload field in the product to prove none is ever
        // read as a tenant id. If an endpoint ever does take one off a payload, this exemption becomes
        // silently wrong about the tenant-scoping column on a schema where tenant_id leads nearly every
        // key - so that change is the one that has to come back and delete this entry.
        "tenant_id",
    };

    /// <summary>
    /// Per-table exemptions, for a single column that is a string key somewhere but genuinely does not need
    /// byte-ordinal agreement. Empty today. An entry here needs a written reason about the VALUE.
    /// </summary>
    private static readonly HashSet<(string Table, string Column)> CollationExceptions = new();

    /// <summary>
    /// INHERITED DEBT, NOT REASONED EXEMPTIONS - and the distinction is the point of keeping them apart.
    ///
    /// These string key columns carry no explicit "C" collation and were already in the schema when this
    /// check was inverted on 2026-09-02. The previous version was an allow-list, so it could not see a
    /// MISSING collation at all and none of these was ever raised. They are listed here so the check is red
    /// for anything NEW - which is the direction that matters - without this one change becoming a
    /// schema-wide edit to five other missions' tables.
    ///
    /// EVERY ENTRY IS AN OPEN QUESTION, not a decision. Several look like they should simply be collated:
    /// workflows.Id and skill_versions.SkillId are slugs of exactly the shape as skills.Id, which IS
    /// collated. The correct resolution for each is either a UseCollation("C") in the model with its
    /// migration, or a written reason moved up into <see cref="CollationExceptions"/> - not indefinite
    /// residence here.
    ///
    /// A NEW column may NOT be added to this set. If a change makes this list shorter, delete the entries
    /// it fixed; if a change would make it longer, that is the check working and the answer is a collation
    /// or an argued exemption.
    /// </summary>
    private static readonly HashSet<(string Table, string Column)> InheritedUncollatedKeyColumns = new()
    {
        ("cron_jobs", "CronJobEntityId"),
        ("cron_jobs", "CronJobEntityTenantId"),
        ("cron_jobs", "Id"),
        ("dictation_transcripts", "Id"),
        // entitlements is the payment side's table, excluded from our migrations - we do not create it, so
        // we cannot collate it either. This one is arguably a genuine exemption rather than debt, but it is
        // left here rather than promoted, because promoting it is a claim about somebody else's schema.
        ("entitlements", "subject"),
        ("repo_state", "DirectorId"),
        ("repo_state", "RepoPath"),
        ("session_keys", "TenantId"),
        ("skill_placement_state", "AgentKind"),
        ("skill_placement_state", "DirectorId"),
        ("skill_versions", "SkillId"),
        ("workflow_versions", "WorkflowId"),
        ("workflows", "Id"),
    };

    /// <summary>
    /// The other side of the same coin: columns carrying an explicit "C" collation while NOT being a string
    /// key column derived from the model. An entry here is a collation applied for a reason the model does
    /// not express, which needs writing down. Every entry carries its argument; a new one without one is
    /// not an exception, it is an unexplained collation.
    /// </summary>
    private static readonly HashSet<(string Table, string Column)> CollationExtras = new()
    {
        // A LOOKUP column, not a key: a credential is FOUND by its hash, and that lookup is an exact
        // byte-ordinal match on a value the caller supplies. Getting equality wrong here does not reorder a
        // list, it authenticates the wrong device or fails to authenticate the right one - so it is
        // deliberately collated even though the model does not make it a key.
        ("device_credentials", "DeviceKeyHash"),

        // The turn store's generation digest. It is part of a NON-unique index rather than a key, so the
        // model-derived enumeration cannot see it, but the store compares it for exact equality on every
        // push to decide whether a batch belongs to the session's current conversation - the same
        // byte-ordinal requirement as the key columns beside it.
        ("session_turn_heads", "Generation"),

        // The trial-extension ledger's subject. Not a key here, but it must group by EXACTLY the value
        // account_trials.subject is keyed on - and that one IS collated. Two columns holding one identity
        // have to agree about equality or the ledger silently reports on a different account than the trial
        // row it is about.
        ("trial_extensions", "subject"),

        // The known-repository lookup keys. Normalized machine and path values that a caller supplies and
        // that are matched as EXACT indexed predicates, so the two providers have to agree on which of them
        // are equal or a search returns a different candidate set on the hosted Gateway than on a local
        // install. They sit in a NON-unique index rather than a key, so the model-derived enumeration
        // cannot see them - which is exactly the case this list exists for.
        //
        // They surfaced here rather than being noticed: they arrived on main while this mission was
        // replacing the hand-written allow-list with the derived check, so the two changes met for the
        // first time in a merge. The check did its job - it is loud about a collation that the model does
        // not account for - and the answer is this written exception, not a wider net.
        ("known_repositories", "MachineKey"),
        ("known_repositories", "PathKey"),
    };

    /// <summary>
    /// Proves that EVERY string key column the model declares carries an explicit byte-ordinal collation
    /// "C" in the live catalog, and that nothing else does. This reads pg_attribute.attcollation (the
    /// column's DEFINED collation), which is the type-default pseudo-collation ("default") for a plain text
    /// column and "C" only where the migration set COLLATE "C" explicitly - so it distinguishes our explicit
    /// collation from the database's own default even on a container whose default already happens to be C.
    ///
    /// The population is DERIVED FROM THE MODEL. It was an allow-list until 2026-09-02, and the direction
    /// mattered: an allow-list is loud when a collation is ADDED and silent when one is MISSING, which is
    /// backwards. The list went stale twice, this suite was red on main from 2026-08-05 through the v2.0.0
    /// and v2.0.1 tags, and a check that cries wolf in the harmless direction stops being read in the
    /// harmless direction - after which it is simply silent. It then missed a real one:
    /// session_screens.SessionId shipped with no collation at all and this test could not have said so.
    /// </summary>
    [RequiresPostgresFact]
    public void Collation_ExplicitC_OnEveryStringKeyColumnTheModelDeclares_OnRealPostgres()
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

        // THE POPULATION IS DERIVED FROM THE MODEL, NOT WRITTEN OUT BY HAND, and what stays hand-written
        // is INVERTED into a short exception list. That inversion is the whole point of this rewrite.
        //
        // The old shape was an allow-list: "these columns are checked", so anything absent was simply
        // unchecked. It was therefore LOUD in the harmless direction (a collation ADDED without updating
        // the list) and SILENT in the dangerous one (a key column with NO collation at all). This file's
        // own history records the cost of that: the list went stale twice, and the suite was red on main
        // from 2026-08-05 through the v2.0.0 and v2.0.1 tags. A check that cries wolf in the harmless
        // direction gets ignored in the harmless direction - and after that it is just silent. The Terminal
        // Rules mission's session_screens.SessionId shipped with no collation and this test could not have
        // said so.
        //
        // NATURAL KEY is a property of the MODEL, so it is read off the model: every string property that
        // participates in a primary key or a unique index. A new key column with no collation and no
        // written exception is now RED, which is the direction that matters.
        var required = ctx.Model.GetEntityTypes()
            .Where(et => et.GetTableName() is not null)
            .SelectMany(et =>
            {
                var table = et.GetTableName()!;
                var storeObject = StoreObjectIdentifier.Table(table, et.GetSchema());
                var keyed = new HashSet<IProperty>();
                foreach (var key in et.GetKeys())
                    foreach (var prop in key.Properties) keyed.Add(prop);
                foreach (var index in et.GetIndexes().Where(ix => ix.IsUnique))
                    foreach (var prop in index.Properties) keyed.Add(prop);
                return keyed
                    .Where(prop => prop.ClrType == typeof(string))
                    .Select(prop => (Table: table, Column: prop.GetColumnName(storeObject) ?? prop.Name));
            })
            .Where(pair => !ExemptColumnNames.Contains(pair.Column)
                           && !CollationExceptions.Contains(pair)
                           && !InheritedUncollatedKeyColumns.Contains(pair))
            .Distinct()
            .OrderBy(pair => pair.Table, StringComparer.Ordinal)
            .ThenBy(pair => pair.Column, StringComparer.Ordinal)
            .ToList();

        // An empty derived set would make every assertion below vacuous - a broken instrument reading as a
        // clean run. This catches ZERO and only zero, though: a derivation that returned three columns
        // instead of twenty-one would sail straight through it. What catches a PARTIALLY broken derivation
        // is the reverse comparison further down, so read that one as load-bearing rather than as tidiness.
        Assert.NotEmpty(required);

        var actual = withExplicitC.ToHashSet();
        var missing = required.Where(pair => !actual.Contains((pair.Table, pair.Column))).ToList();
        Assert.True(missing.Count == 0,
            "these string key columns carry NO explicit C collation, so Postgres would collate them by "
            + "locale while SQLite compares raw bytes and the two providers would disagree on uniqueness: "
            + string.Join(", ", missing.Select(m => m.Table + "." + m.Column))
            + ". Add UseCollation(\"C\") in GatewayDbContext, or add a written exception here saying why not.");

        // THE REVERSE COMPARISON, AND IT IS LOAD-BEARING - do not delete it as redundant. It catches two
        // different things. The obvious one: a column carrying C that is not a derived key column is an
        // accident, and an accidental collation should be as loud as a missing one.
        //
        // The one that matters more: this is the ONLY thing here that catches a derivation which silently
        // stopped finding most of what it should. If the enumeration above breaks and returns a handful of
        // columns instead of all of them, the missing-collation check above still passes on that handful and
        // Assert.NotEmpty is satisfied - but every column that still carries C in the live catalog and has
        // dropped out of "required" surfaces HERE, at once. Remove this and a half-broken derivation reads
        // as a green run.
        var unexpected = actual
            .Where(pair => !required.Contains(pair) && !CollationExtras.Contains(pair))
            .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToList();
        Assert.True(unexpected.Count == 0,
            "these columns carry an explicit C collation but are neither a string key column nor a listed "
            + "exception: " + string.Join(", ", unexpected.Select(u => u.Item1 + "." + u.Item2)));

        // The inherited-debt list is itself hand-kept, so it gets the same treatment as everything else
        // here: an entry that HAS since been collated must be deleted rather than left, or the list slowly
        // becomes a place where fixed things are still recorded as broken and nobody trusts it.
        var fixedSince = InheritedUncollatedKeyColumns.Where(pair => actual.Contains(pair)).ToList();
        Assert.True(fixedSince.Count == 0,
            "these columns are listed as inherited debt but now DO carry an explicit C collation - delete "
            + "them from InheritedUncollatedKeyColumns: "
            + string.Join(", ", fixedSince.Select(f => f.Table + "." + f.Column)));


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
    /// THE PROPERTY THE BYTE-ORDINAL COLLATION ON session_screens.SessionId BUYS, asserted on REAL Postgres
    /// rather than inferred from the model: the screen store is idempotent on its natural key, and it draws
    /// the line between "the same session" and "a different one" in the same place SQLite does.
    ///
    /// The store is keyed (tenant, session, captured-at, director) so the SAME Director re-sending a capture
    /// after a reconnect stores ONE row - and so two Directors capturing one session id at one instant keep
    /// both, which the key could not express before inspection 01's finding 3. That guarantee is only as
    /// good as the database agreeing with SQLite about which session ids are equal - which is what
    /// COLLATE "C" is for, and why this column shipping with no collation was a real defect rather than a
    /// tidiness one. Both appends below use the SAME Director, so the Director is held constant and the
    /// session id is the only thing varying.
    ///
    /// Two halves, and the second is the one that discriminates. The same id twice must be ONE row - but so
    /// it would be under any collation, so on its own that says nothing about collation. Two ids differing
    /// only in CASE must be TWO rows: that is byte-ordinal behaviour, it matches SQLite's BINARY default
    /// exactly, and it is the answer a case-insensitive collation would get wrong.
    /// </summary>
    [RequiresPostgresFact]
    public void SessionScreens_IdempotentOnTheNaturalKey_AndByteOrdinalAboutIt_OnRealPostgres()
    {
        EnsureMigrated();
        var store = new CcDirector.Gateway.Screens.SessionScreenStore(NewContext);
        var capturedAt = new DateTime(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc);
        var lower = "screen-idem-" + Guid.NewGuid().ToString("N");
        var upper = lower.ToUpperInvariant();

        ScreenPush Push(string sessionId, string row) => new()
        {
            SessionId = sessionId,
            CapturedAtUtc = capturedAt,
            Rows = new List<string> { row },
            HasGrid = true,
            BufferBytes = 10,
            ActivityState = "WaitingForInput",
            Agent = "ClaudeCode",
        };

        // Half one: the same capture twice is ONE row, and the second call SAYS it stored nothing rather
        // than throwing or silently duplicating.
        Assert.True(store.Append("d-idem", Push(lower, "first"), DateTime.UtcNow));
        Assert.False(store.Append("d-idem", Push(lower, "first"), DateTime.UtcNow));

        // Half two, the discriminating one: an id differing only in case is a DIFFERENT session, exactly as
        // it is under SQLite's BINARY default. A case-insensitive collation would refuse this write as a
        // duplicate, and then one session's screens would answer another session's reads.
        Assert.True(store.Append("d-idem", Push(upper, "second"), DateTime.UtcNow));

        Assert.Equal(new[] { "first" }, store.ReadLatest(lower)!.Grid.Rows);
        Assert.Equal(new[] { "second" }, store.ReadLatest(upper)!.Grid.Rows);
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
    private static void GuardThrowawayDatabase() => PostgresProofDatabase.GuardThrowawayDatabase();

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
