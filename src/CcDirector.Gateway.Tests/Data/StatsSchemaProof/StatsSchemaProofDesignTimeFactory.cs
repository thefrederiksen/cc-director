using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CcDirector.Gateway.Tests.Data.StatsSchemaProof;

/// <summary>
/// Design-time factory for <see cref="StatsSchemaProofDbContext"/>, used ONLY by <c>dotnet ef migrations
/// add</c> to generate this proof rig's migration chain. The connection string below is a placeholder:
/// generating a migration never opens a connection, it only needs a provider so the SQL dialect is
/// Postgres. The tests supply the real connection string from the rig at run time and never use this.
/// </summary>
public sealed class StatsSchemaProofDesignTimeFactory : IDesignTimeDbContextFactory<StatsSchemaProofDbContext>
{
    public StatsSchemaProofDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StatsSchemaProofDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=55433;Database=ccpgstats;Username=gateway_app_local;Password=design-time-placeholder",
                npg =>
                {
                    // The migration chain lives in this test assembly - it is proof-rig scaffolding and
                    // has no business in a shipping assembly.
                    npg.MigrationsAssembly(typeof(StatsSchemaProofDbContext).Assembly.GetName().Name);
                    npg.MigrationsHistoryTable(
                        StatsSchemaProofDbContext.HistoryTableName,
                        StatsSchemaProofDbContext.SchemaName);
                })
            .Options;
        return new StatsSchemaProofDbContext(options);
    }
}
