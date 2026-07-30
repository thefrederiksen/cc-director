using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// A <see cref="GatewayStatsDbContext"/> factory over an ALREADY-OPEN SQLite connection - the self-host
/// statistics file the <see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase"/> opened and created the
/// tables in.
///
/// It exists because the read projections were ported to Entity Framework BEFORE the startup wiring that
/// selects a provider (Step 2, worker 6). The aggregator is handed a SQLite database today, so this hands
/// the ported reads a context over exactly that database - the same connection, the same rows, the same
/// transaction visibility. It is a SEAM, not a fallback: there is no second store, nothing is retried, and
/// nothing degrades. When the provider selection lands, a Postgres factory is injected here instead and this
/// type serves only the self-host path.
///
/// The connection is OWNED BY THE CALLER. Entity Framework never closes an externally-supplied open
/// connection, and <see cref="GatewayStatsDbContext"/> instances handed out here are disposed per operation
/// while the connection outlives them all.
///
/// NOT pooled. A pooled factory resets and re-uses context instances, which assumes each has its own
/// connection; every context here shares one, and every use of it is already serialised behind the
/// aggregator's lock.
/// </summary>
internal sealed class GatewayStatsSqliteContextFactory : IDbContextFactory<GatewayStatsDbContext>
{
    private readonly SqliteConnection _connection;

    public GatewayStatsSqliteContextFactory(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public GatewayStatsDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(_connection)
            .Options);
}
