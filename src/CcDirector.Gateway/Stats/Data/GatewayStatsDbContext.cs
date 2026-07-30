using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The Gateway STATISTICS database context - the store that used to be <c>gateway-stats.db</c> (SQLite) plus
/// <c>gateway-concurrency-stats.json</c> (a file on the shared file share). Separate from
/// <see cref="Data.GatewayDbContext"/> on purpose: its own schema (<c>gateway_stats</c> on Postgres), its own
/// migration history table and its own connection pool, so a statistics problem can never take the roster
/// down with it.
///
/// MERGE NOTE - this file is worker 5's slice of a sixteen-plus-three-table context, not the finished
/// article. Worker 2 owns the sixteen tables carried over from SQLite schema version 5 and owns the
/// migrations for the whole context. Only the three concurrency tables are declared here, and their
/// configuration is one call (<see cref="ConcurrencyStatsModel.Configure"/>), so folding the two halves
/// together is three <c>DbSet</c> lines and one call - not a hand-reconciled model.
///
/// Deliberately NO migration is generated from this slice. A migration describes the WHOLE model, so one
/// scaffolded against three tables would have to be thrown away the moment the other sixteen arrive.
///
/// Conventions, from the Step 2 entity contract: table and column names are snake_case and identical to
/// schema version 5, configured explicitly rather than by a naming convention; the tenant column is named
/// <c>tenant</c> (not <c>tenant_id</c> - that is the other context's convention and this store does not
/// share it); there are no navigation properties and no foreign keys between these tables.
///
/// There is no global tenant query filter on this context and the concurrency store does not rely on one:
/// every read and every write names its tenant explicitly in the predicate.
/// </summary>
public class GatewayStatsDbContext : DbContext
{
    /// <summary>The Postgres schema this store's tables and its migration history live in. Postgres only -
    /// SQLite has no schemas and uses the default.</summary>
    public const string PostgresSchema = "gateway_stats";

    public GatewayStatsDbContext(DbContextOptions<GatewayStatsDbContext> options) : base(options)
    {
    }

    /// <summary>All-time concurrency peaks, one row per tenant (<c>concurrency_peak</c>).</summary>
    public DbSet<ConcurrencyPeakEntity> ConcurrencyPeaks => Set<ConcurrencyPeakEntity>();

    /// <summary>The per-hour fleet activity log (<c>concurrency_hour</c>).</summary>
    public DbSet<ConcurrencyHourEntity> ConcurrencyHours => Set<ConcurrencyHourEntity>();

    /// <summary>The raw members of each hour's distinct sets (<c>concurrency_hour_member</c>) - restart
    /// durability for the in-memory dedup sets, nothing else.</summary>
    public DbSet<ConcurrencyHourMemberEntity> ConcurrencyHourMembers => Set<ConcurrencyHourMemberEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The statistics store gets its OWN schema on Postgres so it never shares a namespace (or a
        // migration history table) with the Gateway's main context. SQLite has no schemas, so it stays on
        // the default and the on-disk self-host file keeps the table names it already has.
        if (Database.IsNpgsql())
            modelBuilder.HasDefaultSchema(PostgresSchema);

        ConcurrencyStatsModel.Configure(modelBuilder);
    }
}
