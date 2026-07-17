using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Design-time factory used ONLY by the EF Core tooling (<c>dotnet ef migrations</c>) to construct a
/// <see cref="GatewayDbContext"/> when generating or scaffolding migrations. It is never used at runtime -
/// the running Gateway builds the context through <see cref="GatewayDatabase"/>'s pooled factory. The
/// SQLite provider is wired here (with a throwaway data source) so the tooling produces the SQLite
/// migration set; the connection is never opened during scaffolding.
/// </summary>
public sealed class GatewayDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite("Data Source=gateway-design-time.db")
            .Options;
        return new GatewayDbContext(options);
    }
}
