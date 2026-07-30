using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Design-time factory used ONLY by the EF Core tooling (<c>dotnet ef migrations</c>) to construct a
/// <see cref="GatewayDbContext"/> when generating or scaffolding migrations. It is never used at runtime -
/// the running Gateway builds the context through <see cref="GatewayDatabase"/>'s pooled factory.
///
/// One factory, switched by the <c>CC_GATEWAY_EF_PROVIDER</c> environment variable, wires either provider so
/// the same tooling scaffolds either migration set. There is exactly ONE
/// <see cref="IDesignTimeDbContextFactory{TContext}"/> for THIS context in any assembly the tooling scans at
/// once: two for the same context in one scan would make the EF tooling ambiguous ("More than one DbContext
/// factory was found"). The statistics store has its own context and its own factories
/// (<see cref="Stats.Data.GatewayStatsDbContextDesignTimeFactory"/> for SQLite here, and a Postgres one in
/// the CcDirector.Gateway.Migrations.Postgres project) - a different context, and never scanned in the same
/// pass; the tooling picks between contexts from <c>--context</c>, which every documented command passes. Set
/// <c>CC_GATEWAY_EF_PROVIDER=postgres</c> to
/// scaffold the Postgres migration set (into the CcDirector.Gateway.Migrations.Postgres assembly); leave it
/// unset for the default SQLite set (in this project's Data/Migrations).
///
/// Both connection strings are THROWAWAY - <c>migrations add</c> builds the model and writes source; it never
/// opens the connection - so the design values below are never connected to and carry no real credentials.
/// </summary>
public sealed class GatewayDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();

        var provider = Environment.GetEnvironmentVariable("CC_GATEWAY_EF_PROVIDER");
        if (string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseNpgsql(
                "Host=localhost;Database=design;Username=design;Password=design",
                o =>
                {
                    o.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                    o.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
                });
        }
        else
        {
            builder.UseSqlite("Data Source=gateway-design-time.db");
        }

        return new GatewayDbContext(builder.Options);
    }
}
