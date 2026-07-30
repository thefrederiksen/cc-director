using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The Gateway statistics store as an Entity Framework model: the sixteen tables that
/// <see cref="GatewayStatsDatabase"/> creates by hand at SQLite schema version 5, carried forward UNCHANGED
/// so one implementation serves SQLite (self-host) and PostgreSQL (hosted) from the same code.
///
/// That "one implementation, two providers" is the whole point of the port, and it is what makes a
/// provider-parametrised contract suite mean anything: it is one implementation run twice, not two
/// implementations compared.
///
/// THE NAMES ARE NOT NEGOTIABLE. Every table and column name is the version 5 name, in snake_case, and each
/// is pinned EXPLICITLY with ToTable / HasColumnName rather than left to a naming convention. A convention
/// is a rule that has to keep holding; an existing self-host gateway-stats.db is already on disk with these
/// exact names, and a convention change - or one property renamed by a well-meaning hand - would strand it.
/// Pinning them removes the rule.
///
/// WHAT IS DELIBERATELY ABSENT, so nobody adds it back as an improvement:
///
///  - NO navigation properties and NO foreign keys. Version 5 has none between these tables, the identity
///    map that relates them is held in memory, and adding them would change delete and insert ordering.
///  - NO global query filter on the tenant. Two of the sixteen tables (<c>repo_session</c>,
///    <c>agent_session</c>) genuinely have no tenant column at version 5 - they are partitioned indirectly
///    through their per-tenant surrogate ids - so a uniform filter cannot exist here. Scoping is the read and
///    write paths' job, and the contract suite asserts it, including asserting the accessors that currently
///    return every tenant's rows so that whenever that is changed the test turns red and NAMES the change
///    rather than passing silently.
///  - NO unique constraint on any identity table's display column, on either provider. Identity there is
///    case-INSENSITIVE and a database can only enforce it under some collation; see
///    <see cref="RepoIdentityEntity"/> for the full reasoning.
///
/// Threading: used through a pooled IDbContextFactory - a fresh context per operation, never shared across
/// threads.
/// </summary>
public sealed class GatewayStatsDbContext : DbContext
{
    /// <summary>The PostgreSQL schema this context owns. Its migration history table lives in the same
    /// schema (<c>gateway_stats.__EFMigrationsHistory</c>), so the statistics chain never shares a history
    /// table, a transaction or a startup gate with the main <c>gateway</c> schema's chain. SQLite is
    /// schemaless and uses none of this.</summary>
    public const string PostgresSchema = "gateway_stats";

    /// <summary>
    /// The database-level default on the <c>tenant</c> column of the eight delta and identity tables.
    ///
    /// It is in the MODEL, not only in the baseline's raw data definition language, and that distinction is
    /// what a review caught. Schema version 5 added this column with
    /// <c>ALTER TABLE ... ADD COLUMN tenant TEXT NOT NULL DEFAULT 'local'</c>, so the default is part of the
    /// shape of every self-host file on disk. Writing the correct text in the baseline while leaving the
    /// model silent about it hid the divergence from the baseline's output WITHOUT correcting the chain's
    /// target model - and the model is what a LATER migration is scaffolded against. The first migration
    /// needing a table rebuild would have diffed against a snapshot that does not know about this default and
    /// quietly dropped it.
    ///
    /// The eight tables that carry it are exactly the ones version 5 reached by ALTER TABLE. The high-water,
    /// membership and meta tables were REBUILT with the tenant in their primary key and have no default, so
    /// they must not be given one here.
    /// </summary>
    public const string TenantColumnDefault = "local";

    public GatewayStatsDbContext(DbContextOptions<GatewayStatsDbContext> options) : base(options)
    {
    }

    /// <summary>Observed human input deltas (<c>stat_delta</c>), append only.</summary>
    public DbSet<StatDeltaEntity> StatDeltas => Set<StatDeltaEntity>();

    /// <summary>Observed token-spend increases (<c>token_delta</c>), append only.</summary>
    public DbSet<TokenDeltaEntity> TokenDeltas => Set<TokenDeltaEntity>();

    /// <summary>Observed per-agent tally deltas (<c>agent_delta</c>), append only.</summary>
    public DbSet<AgentDeltaEntity> AgentDeltas => Set<AgentDeltaEntity>();

    /// <summary>Observed agent-to-agent deltas (<c>agent_driven_delta</c>), append only.</summary>
    public DbSet<AgentDrivenDeltaEntity> AgentDrivenDeltas => Set<AgentDrivenDeltaEntity>();

    /// <summary>Repository surrogate id to first-seen display spelling (<c>repo_identity</c>).</summary>
    public DbSet<RepoIdentityEntity> RepoIdentities => Set<RepoIdentityEntity>();

    /// <summary>Agent surrogate id to first-seen display spelling (<c>agent_identity</c>).</summary>
    public DbSet<AgentIdentityEntity> AgentIdentities => Set<AgentIdentityEntity>();

    /// <summary>Model surrogate id to first-seen display spelling (<c>model_identity</c>).</summary>
    public DbSet<ModelIdentityEntity> ModelIdentities => Set<ModelIdentityEntity>();

    /// <summary>Checkout surrogate id to first-seen display spelling (<c>checkout_identity</c>).</summary>
    public DbSet<CheckoutIdentityEntity> CheckoutIdentities => Set<CheckoutIdentityEntity>();

    /// <summary>Per-session per-bucket high-water counts (<c>session_highwater</c>).</summary>
    public DbSet<SessionHighwaterEntity> SessionHighwater => Set<SessionHighwaterEntity>();

    /// <summary>Per-session cumulative token high-water counts (<c>token_highwater</c>).</summary>
    public DbSet<TokenHighwaterEntity> TokenHighwater => Set<TokenHighwaterEntity>();

    /// <summary>Per-session agent-driven high-water counts (<c>agent_driven_highwater</c>).</summary>
    public DbSet<AgentDrivenHighwaterEntity> AgentDrivenHighwater => Set<AgentDrivenHighwaterEntity>();

    /// <summary>The all-time set of voice-mode sessions (<c>wingman_session</c>).</summary>
    public DbSet<WingmanSessionEntity> WingmanSessions => Set<WingmanSessionEntity>();

    /// <summary>The set of sessions already back-filled to their agent (<c>agents_seeded</c>).</summary>
    public DbSet<AgentsSeededEntity> AgentsSeeded => Set<AgentsSeededEntity>();

    /// <summary>The all-time repository-to-session membership set (<c>repo_session</c>). No tenant column -
    /// see <see cref="RepoSessionEntity"/>.</summary>
    public DbSet<RepoSessionEntity> RepoSessions => Set<RepoSessionEntity>();

    /// <summary>The all-time agent-to-session membership set (<c>agent_session</c>). No tenant column - see
    /// <see cref="AgentSessionEntity"/>.</summary>
    public DbSet<AgentSessionEntity> AgentSessions => Set<AgentSessionEntity>();

    /// <summary>Runtime scalars keyed by (tenant, name) (<c>meta</c>).</summary>
    public DbSet<MetaEntity> Meta => Set<MetaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- Delta tables - append only -----------------------------------------------------------

        modelBuilder.Entity<StatDeltaEntity>(b =>
        {
            b.ToTable("stat_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Modality).HasColumnName("modality").IsRequired();
            b.Property(e => e.Surface).HasColumnName("surface").IsRequired();
            b.Property(e => e.IsVoice).HasColumnName("is_voice").IsRequired();
            b.Property(e => e.RepoId).HasColumnName("repo_id").IsRequired();
            b.Property(e => e.Wingman).HasColumnName("wingman").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            // Nullable exactly where version 5 is nullable, and nowhere else. Both reached the table by ALTER
            // TABLE (model_id at version 2, checkout_id at version 4), and model_id's nullability is load
            // bearing at runtime as well - see the entity.
            b.Property(e => e.ModelId).HasColumnName("model_id");
            b.Property(e => e.CheckoutId).HasColumnName("checkout_id");
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            // Index names are pinned to the version 5 names with HasDatabaseName. Entity Framework would
            // otherwise mint IX_stat_delta_hour_utc, which is a DIFFERENT index from the ix_stat_delta_hour
            // already on every self-host file - so an adopted store would carry both the old index under its
            // old name and, on the next migration, a new one beside it.
            b.HasIndex(e => e.HourUtc).HasDatabaseName("ix_stat_delta_hour");
            b.HasIndex(e => new { e.Tenant, e.HourUtc }).HasDatabaseName("ix_stat_delta_tenant_hour");
        });

        modelBuilder.Entity<TokenDeltaEntity>(b =>
        {
            b.ToTable("token_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.ModelId).HasColumnName("model_id");
            b.Property(e => e.InputTokens).HasColumnName("input_tokens").IsRequired();
            b.Property(e => e.OutputTokens).HasColumnName("output_tokens").IsRequired();
            b.Property(e => e.CacheReadTokens).HasColumnName("cache_read_tokens").IsRequired();
            b.Property(e => e.CacheCreationTokens).HasColumnName("cache_creation_tokens").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => e.HourUtc).HasDatabaseName("ix_token_delta_hour");
            b.HasIndex(e => new { e.Tenant, e.HourUtc }).HasDatabaseName("ix_token_delta_tenant_hour");
        });

        modelBuilder.Entity<AgentDeltaEntity>(b =>
        {
            b.ToTable("agent_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.IsVoice).HasColumnName("is_voice").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        modelBuilder.Entity<AgentDrivenDeltaEntity>(b =>
        {
            b.ToTable("agent_driven_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        // ---- Identity tables - surrogate id to FIRST-SEEN display spelling -------------------------
        //
        // No unique index on any display column, on either provider. Do not add one: identity here is
        // case-INSENSITIVE and a database can only enforce it under some collation, which would be the wrong
        // question asked authoritatively. RepoIdentityEntity carries the full reasoning.

        modelBuilder.Entity<RepoIdentityEntity>(b =>
        {
            b.ToTable("repo_identity");
            b.HasKey(e => e.RepoId);
            b.Property(e => e.RepoId).HasColumnName("repo_id").ValueGeneratedOnAdd();
            b.Property(e => e.RepoDisplay).HasColumnName("repo_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        modelBuilder.Entity<AgentIdentityEntity>(b =>
        {
            b.ToTable("agent_identity");
            b.HasKey(e => e.AgentId);
            b.Property(e => e.AgentId).HasColumnName("agent_id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentDisplay).HasColumnName("agent_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        modelBuilder.Entity<ModelIdentityEntity>(b =>
        {
            b.ToTable("model_identity");
            b.HasKey(e => e.ModelId);
            b.Property(e => e.ModelId).HasColumnName("model_id").ValueGeneratedOnAdd();
            b.Property(e => e.ModelDisplay).HasColumnName("model_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        modelBuilder.Entity<CheckoutIdentityEntity>(b =>
        {
            b.ToTable("checkout_identity");
            b.HasKey(e => e.CheckoutId);
            b.Property(e => e.CheckoutId).HasColumnName("checkout_id").ValueGeneratedOnAdd();
            b.Property(e => e.CheckoutDisplay).HasColumnName("checkout_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        // ---- High-water tables - the read-modify-write paths ---------------------------------------
        //
        // The tenant is the FIRST key column on all three, as version 5 rebuilt them. Without it in the key,
        // two tenants pushing the same bare session id collide and one silently overwrites the other.

        modelBuilder.Entity<SessionHighwaterEntity>(b =>
        {
            b.ToTable("session_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId, e.Modality, e.Surface });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Modality).HasColumnName("modality").IsRequired();
            b.Property(e => e.Surface).HasColumnName("surface").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
        });

        modelBuilder.Entity<TokenHighwaterEntity>(b =>
        {
            b.ToTable("token_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.InputTokens).HasColumnName("input_tokens").IsRequired();
            b.Property(e => e.OutputTokens).HasColumnName("output_tokens").IsRequired();
            b.Property(e => e.CacheReadTokens).HasColumnName("cache_read_tokens").IsRequired();
            b.Property(e => e.CacheCreationTokens).HasColumnName("cache_creation_tokens").IsRequired();
        });

        modelBuilder.Entity<AgentDrivenHighwaterEntity>(b =>
        {
            b.ToTable("agent_driven_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
        });

        // ---- Membership tables - all-time distinct sets, never pruned ------------------------------

        modelBuilder.Entity<WingmanSessionEntity>(b =>
        {
            b.ToTable("wingman_session");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        modelBuilder.Entity<AgentsSeededEntity>(b =>
        {
            b.ToTable("agents_seeded");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        // repo_session and agent_session have NO tenant column at version 5. That is carried forward exactly:
        // they are partitioned INDIRECTLY through their per-tenant surrogate ids. Adding a tenant column is a
        // behaviour change and is outside this port's scope - see RepoSessionEntity.
        modelBuilder.Entity<RepoSessionEntity>(b =>
        {
            b.ToTable("repo_session");
            b.HasKey(e => new { e.RepoId, e.SessionId });
            b.Property(e => e.RepoId).HasColumnName("repo_id").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        modelBuilder.Entity<AgentSessionEntity>(b =>
        {
            b.ToTable("agent_session");
            b.HasKey(e => new { e.AgentId, e.SessionId });
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        // ---- Scalar table --------------------------------------------------------------------------

        modelBuilder.Entity<MetaEntity>(b =>
        {
            b.ToTable("meta");
            b.HasKey(e => new { e.Tenant, e.Name });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.Name).HasColumnName("name").IsRequired();
            b.Property(e => e.Value).HasColumnName("value").IsRequired();
        });

        // ---- Provider-specific model shape ---------------------------------------------------------
        //
        // Guarded by Database.IsNpgsql() so the SQLite path is 100% unchanged - a self-host file is already on
        // disk in exactly this shape and its snapshot must not move. IsNpgsql() is available at model-build
        // time because both the runtime pooled factory and the design-time factory construct this context with
        // a concrete provider already selected. This is a provider CONDITIONAL, never a try/catch fallback.
        if (Database.IsNpgsql())
        {
            // 1. This context's OWN schema. SQLite is schemaless (one file, no schema namespace) and needs
            //    none; on Postgres every table goes under gateway_stats, matching the migrations history table
            //    (gateway_stats.__EFMigrationsHistory) the database and the design-time factory pin. It is
            //    deliberately NOT the main context's "gateway" schema: separate schema, separate history
            //    table, separate connection pool, so the statistics chain can never share a transaction or a
            //    startup gate with the chain that gates the deploy.
            modelBuilder.HasDefaultSchema(PostgresSchema);

            // 2. Byte-ordinal collation on EVERY text column. SQLite compares all text with its default BINARY
            //    collation (memcmp), which is what every key, index, range and ordering in the version 5 store
            //    is built on; PostgreSQL's default collation is LOCALE-based. Pinning "C" makes both providers
            //    compare these columns identically, which is what output parity on the same rows requires.
            //
            //    Applied to EVERY text column rather than only the key columns, as ONE uniform rule, because
            //    the alternative is a rule someone has to keep remembering per column. hour_utc alone shows
            //    why: it is not a key, but every hourly and working-day projection RANGES and ORDERS on it as
            //    text, and the ARCHIVE marker sorts against real hour keys. The display columns are never
            //    compared by the database at all, so pinning them too costs nothing and removes the question.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entityType.GetProperties())
                    if (property.ClrType == typeof(string))
                        property.SetCollation("C");
        }
    }
}
