using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CcDirector.Gateway.Migrations.Postgres;

/// <summary>
/// Design-time factory used ONLY by the Entity Framework tooling to construct a
/// <see cref="GatewayStatsDbContext"/> when scaffolding the POSTGRES migration chain, which lives in this
/// project. It is never used at runtime.
///
/// WHY IT LIVES HERE RATHER THAN BESIDE THE CONTEXT. The tooling can only DISCOVER a context that is a type
/// in, has a migration in, or has a design-time factory in the STARTUP assembly. Scaffolding this chain means
/// running with this project as the startup project (it is the only project that has both the model, through
/// its reference to CcDirector.Gateway, and this chain's own output as the migrations assembly). This project
/// holds no context type, and before the first migration exists it holds no migration either - so without a
/// factory here the tooling reports "No DbContext named 'GatewayStatsDbContext' was found" and there is no
/// way in. A factory here makes the context discoverable from a CLEAN CHECKOUT, so the documented command
/// below reproduces the chain rather than depending on a migration that has not been written yet.
///
/// It is also why this is a plain Postgres factory with no environment-variable switch. The statistics
/// context has one factory per provider, each in the project that owns that provider's chain
/// (<see cref="GatewayStatsDbContextDesignTimeFactory"/> is the SQLite one, in CcDirector.Gateway), so the
/// two are never scanned together and the tooling can never be ambiguous about which to use: the project the
/// tooling is pointed at IS the choice of provider.
///
/// The connection string is THROWAWAY - <c>migrations add</c> builds the model and writes source, it never
/// opens the connection - so the design value below is never connected to and carries no credentials.
///
/// The command that regenerates this chain, run from the repository root:
///
/// <code>
/// dotnet ef migrations add &lt;Name&gt; \
///   --project src/CcDirector.Gateway.Migrations.Postgres \
///   --startup-project src/CcDirector.Gateway.Migrations.Postgres \
///   --context GatewayStatsDbContext \
///   --output-dir StatsMigrations
/// </code>
///
/// The reference direction stays Migrations.Postgres -> Gateway ONLY. CcDirector.Gateway must never
/// reference this project back; that would be a cycle, and it is also why the Gateway project cannot be the
/// startup project for this command (the tooling could not load the migrations assembly from its output).
/// </summary>
public sealed class GatewayStatsDbContextPostgresDesignTimeFactory
    : IDesignTimeDbContextFactory<GatewayStatsDbContext>
{
    public GatewayStatsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GatewayStatsDbContext>();
        builder.UseNpgsql(
            "Host=localhost;Database=design;Username=design;Password=design",
            o =>
            {
                o.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                // This context's OWN history table, in its OWN schema. Never the main context's
                // gateway.__EFMigrationsHistory: the two chains must not share a history table, a transaction
                // or a startup gate, so a statistics migration can never gate the deploy.
                o.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            });
        return new GatewayStatsDbContext(builder.Options);
    }
}
