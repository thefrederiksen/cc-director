using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// Design-time factory used ONLY by the Entity Framework tooling (<c>dotnet ef migrations</c>) to construct a
/// <see cref="GatewayStatsDbContext"/> when scaffolding the SQLITE migration chain, which lives in this
/// project. It is never used at runtime - the running Gateway builds the context through its pooled factory.
///
/// SQLITE ONLY, deliberately, and it takes no environment-variable switch - which is where this differs from
/// <see cref="CcDirector.Gateway.Data.GatewayDbContextDesignTimeFactory"/>. The statistics context's Postgres
/// factory is <c>GatewayStatsDbContextPostgresDesignTimeFactory</c>, in the
/// CcDirector.Gateway.Migrations.Postgres project beside the chain it scaffolds.
///
/// The split is not tidiness, it is what makes the Postgres command work at all. The tooling can only
/// DISCOVER a context that is a type in, has a migration in, or has a design-time factory in the startup
/// assembly. Scaffolding the Postgres chain means running with the migrations project as the startup project,
/// and that project holds no context type and - before the first migration exists - no migration either. A
/// factory there is what makes the context discoverable, so the command is reproducible from a clean
/// checkout rather than depending on a migration that is not written yet.
///
/// Having one factory per provider, each in the project that owns that provider's chain, also means the two
/// are never scanned together, so the tooling can never be ambiguous about which one to use: the project the
/// tooling is pointed at IS the choice of provider. Nothing to remember and nothing to set.
///
/// The connection string is THROWAWAY - <c>migrations add</c> builds the model and writes source, it never
/// opens the connection - so the design value below is never connected to and carries no credentials.
///
/// The command that regenerates the SQLite chain, run from the repository root:
///
/// <code>
/// dotnet ef migrations add &lt;Name&gt; \
///   --project src/CcDirector.Gateway \
///   --startup-project src/CcDirector.Gateway \
///   --context GatewayStatsDbContext \
///   --output-dir Stats/Data/Migrations
/// </code>
/// </summary>
public sealed class GatewayStatsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GatewayStatsDbContext>
{
    public GatewayStatsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GatewayStatsDbContext>();
        // No MigrationsAssembly pin: the SQLite chain lives in this project, which is the context's own
        // assembly, so the default is already right.
        builder.UseSqlite("Data Source=gateway-stats-design-time.db");
        return new GatewayStatsDbContext(builder.Options);
    }
}
