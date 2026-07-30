using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// A <see cref="GatewayStatsDbContext"/> factory over an ALREADY-OPEN SQLite connection - the self-host
/// statistics file that <see cref="GatewayStatsDatabase"/> owns, opens and versions.
///
/// It hands out contexts on that ONE connection deliberately. The self-host Gateway is a single process and a
/// single writer; every caller reaches the store under the aggregator's own lock, so a second connection
/// would buy nothing and would put two connections on one file for a store that has exactly one writer. The
/// connection stays the caller's - Entity Framework only closes connections it opened itself, so disposing a
/// context here does not close the file the rest of the class is still reading through.
///
/// The hosted Gateway does NOT use this: it gets a pooled Npgsql factory pointing at the statistics database,
/// and the same <see cref="GatewayStatsWriter"/> runs on it unchanged. That is the whole point of the port -
/// one implementation, two providers, rather than two implementations compared to each other.
/// </summary>
internal sealed class SqliteStatsContextFactory : IDbContextFactory<GatewayStatsDbContext>
{
    private readonly DbContextOptions<GatewayStatsDbContext> _options;

    public SqliteStatsContextFactory(SqliteConnection connection)
    {
        _options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    public GatewayStatsDbContext CreateDbContext() => new(_options);
}
