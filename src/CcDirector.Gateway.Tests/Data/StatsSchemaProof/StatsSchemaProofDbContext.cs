using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tests.Data.StatsSchemaProof;

/// <summary>
/// A deliberately small Entity Framework model used ONLY to prove that a database role holding nothing
/// but CREATE on the database can run a migration chain inside its own <c>gateway_stats</c> schema, with
/// its own history table there. It is a proof rig, not the statistics model: the real
/// <c>GatewayStatsDbContext</c> and its sixteen tables are built separately, and this context must never
/// grow into a second copy of them.
///
/// Two entities, in two migrations, is the point: a single migration would prove a schema can be
/// created, but not that Entity Framework can read its own history table back out of
/// <c>gateway_stats</c> and apply the NEXT migration on top of it. That second read is where a
/// mis-schemed history table shows up, so the chain has to be longer than one.
/// </summary>
public sealed class StatsSchemaProofDbContext : DbContext
{
    /// <summary>The schema the hosted statistics store will own. The proof is that the restricted role
    /// can create THIS, having never owned a schema before.</summary>
    public const string SchemaName = "gateway_stats";

    /// <summary>The migrations history table name Entity Framework uses by default; named here so the
    /// test asserts against the same constant the context is wired with.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    public StatsSchemaProofDbContext(DbContextOptions<StatsSchemaProofDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProofDeltaRow> ProofDeltas => Set<ProofDeltaRow>();

    public DbSet<ProofHighwaterRow> ProofHighwater => Set<ProofHighwaterRow>();

    public DbSet<ProofMetaRow> ProofMeta => Set<ProofMetaRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Everything this context owns lives in gateway_stats. Nothing of it may land in public - the
        // hosted role cannot create there, so a model that leaked a table into public would fail on
        // deploy day rather than here.
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<ProofDeltaRow>(entity =>
        {
            entity.ToTable("proof_delta", SchemaName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            entity.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            entity.Property(e => e.Turns).HasColumnName("turns");
            // Added by the SECOND migration in the chain.
            entity.Property(e => e.Chars).HasColumnName("chars");
            entity.HasIndex(e => new { e.Tenant, e.HourUtc }).HasDatabaseName("ix_proof_delta_tenant_hour");
        });

        modelBuilder.Entity<ProofHighwaterRow>(entity =>
        {
            entity.ToTable("proof_highwater", SchemaName);
            entity.HasKey(e => new { e.Tenant, e.SessionId });
            entity.Property(e => e.Tenant).HasColumnName("tenant");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Turns).HasColumnName("turns");
        });

        // Created by the SECOND migration in the chain, so applying it exercises reading the history
        // table back out of gateway_stats and altering a table already inside it.
        modelBuilder.Entity<ProofMetaRow>(entity =>
        {
            entity.ToTable("proof_meta", SchemaName);
            entity.HasKey(e => new { e.Tenant, e.Name });
            entity.Property(e => e.Tenant).HasColumnName("tenant");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Value).HasColumnName("value").IsRequired();
        });
    }
}

/// <summary>An append-only row, shaped like the real delta tables (string hour, string tenant, long
/// count) so the proof exercises the same column types the statistics store will use.</summary>
public sealed class ProofDeltaRow
{
    public long Id { get; set; }

    public string HourUtc { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public long Turns { get; set; }

    /// <summary>Added by the second migration, so the chain includes an ALTER of a table that already
    /// lives inside gateway_stats and not only fresh CREATEs.</summary>
    public long Chars { get; set; }
}

/// <summary>A composite-key row, shaped like the real high-water tables.</summary>
public sealed class ProofHighwaterRow
{
    public string Tenant { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public long Turns { get; set; }
}

/// <summary>A scalar row, shaped like the real meta table. Created by the second migration.</summary>
public sealed class ProofMetaRow
{
    public string Tenant { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
