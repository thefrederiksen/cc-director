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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CronJobEntity>(b =>
        {
            b.ToTable("cron_jobs");
            b.HasKey(e => e.Id);
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
            b.HasKey(e => e.Id);
            b.Property(e => e.Id);
        });

        modelBuilder.Entity<WorkflowVersionEntity>(b =>
        {
            b.ToTable("workflow_versions");
            b.HasKey(e => e.Id);
            // A workflow's versions are unique per number; lookups go through (WorkflowId, Version).
            b.HasIndex(e => new { e.WorkflowId, e.Version }).IsUnique();
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

        // Tenant scoping - the tenant_id column plus the global query filter - applied uniformly to every
        // entity that derives from TenantScopedEntity, so future stores inherit it by deriving from the base.
        ApplyTenantScope<CronJobEntity>(modelBuilder);
        ApplyTenantScope<CronRunEntity>(modelBuilder);
        ApplyTenantScope<WorkListEntity>(modelBuilder);
        ApplyTenantScope<WorkListItemEntity>(modelBuilder);
        ApplyTenantScope<WorkflowEntity>(modelBuilder);
        ApplyTenantScope<WorkflowVersionEntity>(modelBuilder);
        ApplyTenantScope<WorkflowFileEntity>(modelBuilder);

        ApplyCommonSubsetConventions(modelBuilder);
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
