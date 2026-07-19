using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CcDirector.Gateway.Data;

/// <summary>
/// The Gateway's EF Core data-access context (Hosted Gateway mission, Step 1b). One model runs on SQLite
/// locally and (in a later phase) Postgres in the cloud, so the model is kept provider-AGNOSTIC: no
/// SQLite-only or Postgres-only construct appears here, and the common-subset discipline is baked into the
/// model conventions so every store that moves onto this layer inherits it.
///
/// This context is used through a pooled <c>IDbContextFactory</c> - a fresh context per operation, never
/// shared across threads - and each store keeps its own write lock, preserving the Gateway's single-writer
/// invariant. Because pooling reuses instances, the context takes only its options; the ambient tenant is
/// supplied per operation via <see cref="ActiveTenant"/>, which the store sets from the
/// <see cref="Core.Tenancy.ITenantContext"/> before reading or writing.
///
/// The common-subset conventions (applied in <see cref="OnModelCreating"/>):
///  - Timestamps are stored/read as UTC <see cref="DateTime"/> (never DateTimeOffset - EF's SQLite converter
///    has a cross-timezone bug); a converter forces UTC on the way in and marks the Kind UTC on the way out.
///  - A bare <see cref="decimal"/> mapping is FORBIDDEN (SQLite has no real decimal and EF falls to double,
///    which would silently break the "round up, never undercount" money rule). Cron has none; the guard
///    protects later money-bearing stores.
///  - GUID keys are generated in code, never by a database default.
///  - Every table carries a <c>tenant_id</c> column and a global query filter scoping reads to
///    <see cref="ActiveTenant"/>, so a tenant never reads another tenant's rows. On the single-tenant local
///    install this is always "local" and behavior is identical to a store with no tenant column.
/// </summary>
public sealed class GatewayDbContext : DbContext
{
    /// <summary>
    /// The tenant the current unit of work is scoped to (the raw <see cref="Core.Tenancy.TenantId.Value"/>).
    /// Set by the store from the ambient tenant context before every read or write; the global query filter
    /// compares each row's <c>tenant_id</c> against it. Null only on a context that has not been scoped yet -
    /// the store always sets it, and it fails loud there on an invalid tenant, so a null never reaches a query.
    /// </summary>
    public string? ActiveTenant { get; set; }

    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options)
    {
    }

    /// <summary>Cron job definitions (<c>cron_jobs</c>).</summary>
    public DbSet<CronJobEntity> CronJobs => Set<CronJobEntity>();

    /// <summary>Cron run history (<c>cron_runs</c>), one row per fire.</summary>
    public DbSet<CronRunEntity> CronRuns => Set<CronRunEntity>();

    /// <summary>Named work lists (<c>worklists</c>).</summary>
    public DbSet<WorkListEntity> WorkLists => Set<WorkListEntity>();

    /// <summary>Work-list item references (<c>worklist_items</c>), ordered per list.</summary>
    public DbSet<WorkListItemEntity> WorkListItems => Set<WorkListItemEntity>();

    /// <summary>Workflow head records (<c>workflows</c>) - identity and lifecycle, never content.</summary>
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();

    /// <summary>Immutable workflow content versions (<c>workflow_versions</c>).</summary>
    public DbSet<WorkflowVersionEntity> WorkflowVersions => Set<WorkflowVersionEntity>();

    /// <summary>Helper files belonging to a workflow version (<c>workflow_files</c>).</summary>
    public DbSet<WorkflowFileEntity> WorkflowFiles => Set<WorkflowFileEntity>();

    /// <summary>Workflow runs (<c>workflow_runs</c>) - the governance outcome spine (issue #1771).</summary>
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();

    /// <summary>Pending session snoozes (<c>snoozes</c>), keyed by session id.</summary>
    public DbSet<SnoozeEntity> Snoozes => Set<SnoozeEntity>();

    /// <summary>The append-only governance event ledger (<c>governance_events</c>) - immutable session/run
    /// state transitions, the duration spine of issue #1771.</summary>
    public DbSet<GovernanceEventEntity> GovernanceEvents => Set<GovernanceEventEntity>();

    /// <summary>Web Push subscriptions (<c>push_subscriptions</c>), keyed by browser push endpoint.</summary>
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();

    /// <summary>The wingman instructions state document (<c>wingman_instructions</c>), one row per tenant.</summary>
    public DbSet<WingmanInstructionEntity> WingmanInstructions => Set<WingmanInstructionEntity>();

    /// <summary>Per-session token effort and honest spend (<c>session_spend</c>) - raw tokens + billing-mode
    /// label, the driver-normalized spend of issue #1771 (spine item 3).</summary>
    public DbSet<SessionSpendEntity> SessionSpend => Set<SessionSpendEntity>();

    /// <summary>Account-level hosted-AI service dollars (<c>account_hosted_ai_spend</c>) mirrored from the
    /// cloud credit-debit ledger - real metered dollars, never pinned to a session or run (issue #1771).</summary>
    public DbSet<AccountHostedAiSpendEntity> AccountHostedAiSpend => Set<AccountHostedAiSpendEntity>();

    /// <summary>Mission WHY notes (<c>mission_notes</c>), keyed by the normalized mission name.</summary>
    public DbSet<MissionNoteEntity> MissionNotes => Set<MissionNoteEntity>();

    /// <summary>The append-only governance audit trail (<c>governance_audit_events</c>) - structured
    /// intervention and permission/sandbox decisions, never inferred from transcripts (issue #1771).</summary>
    public DbSet<GovernanceAuditEventEntity> GovernanceAuditEvents => Set<GovernanceAuditEventEntity>();

    /// <summary>The account-to-tenant mapping (<c>tenants</c>), keyed by the verified Supabase subject. This
    /// is the GLOBAL mapping table (Hosted Multi-Tenancy increment 1) - deliberately NOT tenant-scoped, so it
    /// has no <c>tenant_id</c> column and no query filter; it is the table the filter's tenant values come
    /// from. Unused on the single-tenant local install.</summary>
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CronJobEntity>(b =>
        {
            b.ToTable("cron_jobs");
            // COMPOSITE primary key (tenant_id, Id) - Hosted Multi-Tenancy. CronJobStore mints a SHORT id
            // (cj_ + 24 bits) whose uniqueness it checks THROUGH the tenant query filter (per tenant), and the
            // legacy import preserves caller-supplied short ids, so two tenants can hold the same id; scoping
            // the key by tenant lets each tenant own its id space (the store already treats it as per-tenant).
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // The nested target/action are bulky sub-documents: map each as an owned type serialized to a
            // JSON column rather than its own table (the reusable "sub-doc -> JSON in a column" pattern).
            b.OwnsOne(e => e.Target, o => o.ToJson());
            b.OwnsOne(e => e.Action, o => o.ToJson());
        });

        modelBuilder.Entity<CronRunEntity>(b =>
        {
            b.ToTable("cron_runs");
            b.HasKey(e => e.Id);
            // Newest-first within a job is served by ordering on Sequence DESC; index the lookup+order path.
            b.HasIndex(e => new { e.JobId, e.Sequence });
        });

        modelBuilder.Entity<WorkListEntity>(b =>
        {
            b.ToTable("worklists");
            b.HasKey(e => e.Id);
            // Items are an ORDERED child table (worklist_items) with a cascade so a list's items go with it.
            b.HasMany(e => e.Items).WithOne().HasForeignKey(i => i.WorkListId).OnDelete(DeleteBehavior.Cascade);
            // Deliberately NO database-level name-unique index. Name uniqueness is case-insensitive and must
            // match the legacy Dictionary(OrdinalIgnoreCase) exactly, and no stored string transform
            // reproduces StringComparer.OrdinalIgnoreCase (ToUpperInvariant over-merges U+017F onto 'S'), so a
            // unique index over a fold column could not preserve the behaviour and could brick an import. The
            // store enforces uniqueness in code via OrdinalIgnoreCase under its single-writer lock - exactly
            // what the old Dictionary did (which also had no database constraint).
        });

        modelBuilder.Entity<WorkListItemEntity>(b =>
        {
            b.ToTable("worklist_items");
            b.HasKey(e => e.Id);
            // Ordered read + the per-list lookup path (Reorder / RemoveItem) go through (WorkListId, Position).
            b.HasIndex(e => new { e.WorkListId, e.Position });
        });

        modelBuilder.Entity<WorkflowEntity>(b =>
        {
            b.ToTable("workflows");
            // COMPOSITE primary key (tenant_id, Id) - Hosted Multi-Tenancy. The built-in workflow seed uses a
            // FIXED id per built-in (Id = definition.Id), so with Id ALONE as the key two tenants seeding the
            // same built-in collide at the database (the SYSTEM seed vs leftover 'local' built-ins crashed
            // startup). Scoping the key by tenant lets each tenant - and the reserved SYSTEM tenant - hold its
            // own copy of a built-in, and makes an existing single-tenant gateway upgrade automatically (the
            // 'local' and SYSTEM built-ins coexist, the tenant-filtered seed is idempotent).
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // The owner's switch defaults ON at the DATABASE level too, so the migration that adds
            // the column backfills every pre-existing workflow as in-force - upgrading must never
            // silently switch a fleet's workflows off.
            b.Property(e => e.Enabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<WorkflowVersionEntity>(b =>
        {
            b.ToTable("workflow_versions");
            b.HasKey(e => e.Id);
            // A workflow's versions are unique per number; lookups go through (WorkflowId, Version). The
            // uniqueness must be PER TENANT (Hosted Multi-Tenancy): a built-in's WorkflowId + Version=1 is the
            // same across tenants, so a GLOBAL unique index would collide when the SYSTEM seed and a leftover
            // 'local' built-in both hold v1. Scoping the unique index by tenant lets each tenant hold its own
            // v1 of a built-in. (The PK stays the random-Guid Id; only the uniqueness rule is tenant-scoped.)
            b.HasIndex(e => new { e.TenantId, e.WorkflowId, e.Version }).IsUnique();
            // Steps and outcome criteria are bounded sub-documents: owned types serialized to a JSON
            // column each (the cron store's "sub-doc -> JSON in a column" pattern).
            b.OwnsMany(e => e.Steps, o => o.ToJson());
            b.OwnsMany(e => e.OutcomeCriteria, o => o.ToJson());
        });

        modelBuilder.Entity<WorkflowFileEntity>(b =>
        {
            b.ToTable("workflow_files");
            b.HasKey(e => e.Id);
            // Files are read per version; indexed but deliberately NOT a foreign key (independent
            // lifecycle, matching cron_runs -> cron_jobs).
            b.HasIndex(e => e.VersionId);
        });

        modelBuilder.Entity<WorkflowRunEntity>(b =>
        {
            b.ToTable("workflow_runs");
            b.HasKey(e => e.Id);
            // The reporting cuts: per-workflow and per-mission run lists. Indexed, never foreign
            // keys - a run outlives archives and mission cleanup.
            b.HasIndex(e => e.WorkflowId);
            b.HasIndex(e => e.MissionId);
            b.OwnsMany(e => e.CriteriaResults, o => o.ToJson());
            b.OwnsMany(e => e.ProofLinks, o => o.ToJson());
            b.OwnsMany(e => e.Participants, o => o.ToJson());
        });

        modelBuilder.Entity<SnoozeEntity>(b =>
        {
            b.ToTable("snoozes");
            // SessionId is the natural key - a globally unique, ordinally-compared GUID string - so it is the
            // primary key directly (no surrogate id). SQLite's default BINARY text collation compares it
            // ordinally, matching the legacy Dictionary(StringComparer.Ordinal). The armed-vs-deferred
            // invariant is kept in the store, not as a database CHECK (provider-divergent), matching today.
            b.HasKey(e => e.SessionId);
        });

        modelBuilder.Entity<GovernanceEventEntity>(b =>
        {
            b.ToTable("governance_events");
            b.HasKey(e => e.Id);
            // The duration-rollup read paths: a session's transitions over time, a run's, and the weekly
            // whole-week scan across every subject. Each composite LEADS WITH tenant_id because the global
            // query filter prepends "tenant_id = @t" to every query - a SessionId/RunId/OccurredUtc-leading
            // index would force a cross-tenant scan under Phase B multi-tenant load, so these are the
            // tenant-leading forms that actually serve the filtered query. (ApplyTenantScope adds a
            // standalone tenant_id index too; these are the query-serving composites.)
            // SessionId/RunId are value references, never foreign keys - the ledger outlives the run and
            // the session it records.
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.RunId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.OccurredUtc });
        });

        modelBuilder.Entity<PushSubscriptionEntity>(b =>
        {
            b.ToTable("push_subscriptions");
            // Endpoint is the natural key - a unique-per-subscription URL compared ordinally (SQLite's default
            // BINARY collation), matching the legacy Dictionary(StringComparer.Ordinal) - so it is the primary
            // key directly, no surrogate id. There is no per-user column: the legacy shape had none.
            b.HasKey(e => e.Endpoint);
        });

        modelBuilder.Entity<WingmanInstructionEntity>(b =>
        {
            b.ToTable("wingman_instructions");
            b.HasKey(e => e.Id);
            // The document has no external id; the store keeps one row per tenant under its write lock.
            // The saved versions are a bounded sub-document: an owned collection serialized to a JSON column
            // (the cron/workflow "sub-doc -> JSON in a column" pattern), preserving each field and the order.
            b.OwnsMany(e => e.Versions, o => o.ToJson());
        });

        modelBuilder.Entity<SessionSpendEntity>(b =>
        {
            b.ToTable("session_spend");
            // SessionId is the natural key - one row per session, globally-unique GUID string - so it is the
            // primary key directly (the snoozes-table pattern), no surrogate id.
            b.HasKey(e => e.SessionId);
            // The reporting cut is a last-observed time window, tenant-leading for the global filter.
            b.HasIndex(e => new { e.TenantId, e.LastObservedUtc });
        });

        modelBuilder.Entity<AccountHostedAiSpendEntity>(b =>
        {
            b.ToTable("account_hosted_ai_spend");
            b.HasKey(e => e.Id);
            // The dedup lookup (kind + amount + transaction-time) and the weekly window sum, both tenant-
            // leading for the global filter. NOT unique on the dedup tuple on purpose: two genuinely distinct
            // debits of the same amount at the same instant are rare but real, and a unique index would throw
            // on one; the store de-duplicates in code and discloses the (rare) undercount instead of crashing.
            b.HasIndex(e => new { e.TenantId, e.Kind, e.AmountMicros, e.TransactionCreatedUtc });
            b.HasIndex(e => new { e.TenantId, e.TransactionCreatedUtc });
        });

        modelBuilder.Entity<MissionNoteEntity>(b =>
        {
            b.ToTable("mission_notes");
            // Key is the natural key - the already-normalized (trim + lower-cased) mission name - compared
            // ordinally by SQLite's default BINARY collation, matching the legacy Dictionary(StringComparer.
            // Ordinal) keyed by that same normalized value. It is the primary key directly, no surrogate. No
            // case-fold question arises: the value is folded BEFORE it becomes the key, so lookup is a plain
            // ordinal equality (unlike the work-list NAME key, which folds at comparison time).
            //
            // COMPOSITE primary key (tenant_id, Key) - Hosted Multi-Tenancy. The mission NAME is namespaced per
            // tenant, so with Key ALONE as the key two tenants naming a mission the same thing would collide at
            // the database. Scoping the key by tenant lets each tenant own its own note for a given name.
            b.HasKey(e => new { e.TenantId, e.Key });
        });

        modelBuilder.Entity<GovernanceAuditEventEntity>(b =>
        {
            b.ToTable("governance_audit_events");
            b.HasKey(e => e.Id);
            // The read paths: a session's audit trail over time, a run's, and a category scan (all the
            // permission decisions, all the interventions) over a window - each tenant-leading for the
            // global filter. SessionId/RunId are value references, never foreign keys - the trail outlives
            // the session and run it records.
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.RunId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.Category, e.OccurredUtc });
        });

        modelBuilder.Entity<TenantEntity>(b =>
        {
            b.ToTable("tenants");
            // Id is the tenant's own id value (a code-generated GUID string), the natural primary key -
            // ordinally compared (SQLite's default BINARY collation), no surrogate. This table is the GLOBAL
            // account->tenant mapping, so it is deliberately NOT passed to ApplyTenantScope below: it carries
            // no tenant_id column and no query filter (it is the table the filter's tenant values come from).
            b.HasKey(e => e.Id);
            // The mapping is looked up by the verified Supabase subject; it is unique (1:1 account->tenant now,
            // enforced at the database, so a duplicate mint can never split one account across two tenants).
            b.HasIndex(e => e.AccountSubject).IsUnique();
        });

        // Tenant scoping - the tenant_id column plus the global query filter - applied uniformly to every
        // entity that derives from TenantScopedEntity, so future stores inherit it by deriving from the base.
        ApplyTenantScope<CronJobEntity>(modelBuilder);
        ApplyTenantScope<CronRunEntity>(modelBuilder);
        ApplyTenantScope<WorkListEntity>(modelBuilder);
        ApplyTenantScope<WorkListItemEntity>(modelBuilder);
        ApplyTenantScope<WorkflowEntity>(modelBuilder);
        ApplyTenantScope<WorkflowVersionEntity>(modelBuilder);
        ApplyTenantScope<WorkflowFileEntity>(modelBuilder);
        ApplyTenantScope<WorkflowRunEntity>(modelBuilder);
        ApplyTenantScope<SnoozeEntity>(modelBuilder);
        ApplyTenantScope<GovernanceEventEntity>(modelBuilder);
        ApplyTenantScope<PushSubscriptionEntity>(modelBuilder);
        ApplyTenantScope<WingmanInstructionEntity>(modelBuilder);
        ApplyTenantScope<SessionSpendEntity>(modelBuilder);
        ApplyTenantScope<AccountHostedAiSpendEntity>(modelBuilder);
        ApplyTenantScope<MissionNoteEntity>(modelBuilder);
        ApplyTenantScope<GovernanceAuditEventEntity>(modelBuilder);

        ApplyCommonSubsetConventions(modelBuilder);

        // Provider-specific model shape. Guarded by Database.IsNpgsql() so the SQLite path is 100% unchanged
        // (its snapshot must not move) and Postgres alone gets the two things SQLite gives for free. IsNpgsql()
        // is available at model-build time because both the runtime pooled factory and the design-time factory
        // construct this context with a concrete provider already selected.
        if (Database.IsNpgsql())
        {
            // 1. A named default schema. SQLite is schemaless (one file, no schema namespace), so it needs
            //    none; Postgres puts every table under the "gateway" schema, matching the migrations history
            //    table (__EFMigrationsHistory in schema "gateway") the database and factory pin.
            modelBuilder.HasDefaultSchema("gateway");

            // 2. Byte-ordinal collation on the four natural-key string PRIMARY KEY columns. These keys rely on
            //    SQLite's DEFAULT BINARY collation, which compares raw bytes (memcmp) - the same ordering the
            //    legacy Dictionary(StringComparer.Ordinal) had. Postgres' default collation is LOCALE-based,
            //    NOT byte-ordinal, so without this the ordering and the case-of-uniqueness would silently
            //    differ between the two providers. Collation "C" collates raw UTF-8 bytes (memcmp) and SQLite's
            //    default BINARY collation also compares raw UTF-8 bytes, so the two providers AGREE with each
            //    other exactly on both ordering and uniqueness - which is the property we need here. (The only
            //    theoretical divergence is against .NET's UTF-16 StringComparer.Ordinal for rare supplementary-
            //    plane characters, where UTF-8 byte order and UTF-16 code-unit order can differ; that does not
            //    matter here because equality/uniqueness is exact regardless, and any in-memory ordering the
            //    stores do is done in .NET, not the database. So no assumption that these keys are ASCII is
            //    needed - mission_notes.Key in particular is only trimmed and lower-cased, not restricted.)
            modelBuilder.Entity<SnoozeEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<PushSubscriptionEntity>().Property(e => e.Endpoint).UseCollation("C");
            modelBuilder.Entity<SessionSpendEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<MissionNoteEntity>().Property(e => e.Key).UseCollation("C");
            // The tenants mapping table's natural-key string columns (the tenant id primary key and the
            // account_subject unique index) rely on byte-ordinal equality/uniqueness, same as the keys above,
            // so pin them to "C" too - the account subject in particular must match EXACTLY on both providers.
            modelBuilder.Entity<TenantEntity>().Property(e => e.Id).UseCollation("C");
            modelBuilder.Entity<TenantEntity>().Property(e => e.AccountSubject).UseCollation("C");
        }
    }

    /// <summary>Map the tenant column and install the deny-by-default global query filter for one entity.</summary>
    private void ApplyTenantScope<TEntity>(ModelBuilder modelBuilder) where TEntity : TenantScopedEntity
    {
        modelBuilder.Entity<TEntity>(b =>
        {
            b.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
            b.HasIndex(e => e.TenantId);
            // Scope every read to the active tenant. ActiveTenant is a context member, so EF re-evaluates it
            // per query against the context instance running the query - "local" on the single-tenant install.
            b.HasQueryFilter(e => e.TenantId == ActiveTenant);
        });
    }

    /// <summary>The UTC timestamp converter: force UTC in, mark Kind UTC out. Shared by every DateTime column.</summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter = new(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    /// <summary>
    /// Apply the model-wide correctness conventions across every mapped property: UTC DateTime storage, a
    /// hard ban on a bare decimal mapping, and code-generated GUID keys (no database default). These are the
    /// common subset that must hold identically on SQLite and Postgres, so they live on the model, not in a
    /// provider.
    /// </summary>
    private static void ApplyCommonSubsetConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clr = property.ClrType;

                if (clr == typeof(DateTime))
                    property.SetValueConverter(UtcConverter);
                else if (clr == typeof(DateTime?))
                    property.SetValueConverter(NullableUtcConverter);

                if (clr == typeof(decimal) || clr == typeof(decimal?))
                    throw new InvalidOperationException(
                        $"Bare decimal mapping on {entityType.ClrType.Name}.{property.Name} is forbidden: " +
                        "SQLite has no real decimal and EF falls back to double, which would silently break " +
                        "the round-up-never-undercount money rule. Store money as integer smallest-units, or " +
                        "apply an explicit value-converted decimal.");

                if ((clr == typeof(Guid) || clr == typeof(Guid?)) && property.IsPrimaryKey())
                    property.ValueGenerated = ValueGenerated.Never;
            }
        }
    }
}
