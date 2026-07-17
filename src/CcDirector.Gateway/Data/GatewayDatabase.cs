using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Owns the Gateway's EF Core database (gateway.db): builds the pooled context factory, applies the EF
/// migrations at startup, and hands out a tenant-scoped context per operation. One instance for the whole
/// Gateway, constructed by the host and shared by every store that has moved onto the EF data layer.
///
/// Fail-loud, no fallback (mission rule, matching <see cref="Stats.GatewayStatsDatabase"/>): if the database
/// cannot be opened or migrated, this throws with a clear message and the Gateway does not start half-blind.
/// It never falls back to the old JSON stores - coming up empty or silently reverting is the failure mode
/// this mission exists to end.
///
/// Threading: the Gateway is a single process and single writer. Contexts come from a pooled factory (a
/// fresh context per operation, never shared across threads) and each store keeps its own write lock, which
/// together preserve the single-writer invariant while letting WAL readers run.
/// </summary>
public sealed class GatewayDatabase : IDisposable
{
    private readonly string _path;
    private readonly ITenantContext _tenant;
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<GatewayDbContext> _factory;
    private bool _disposed;

    /// <summary>The database file path, for logging.</summary>
    public string Path => _path;

    /// <param name="tenant">The ambient tenant context. On the local install this is
    /// <see cref="SingleTenantContext"/> and every row is the "local" tenant.</param>
    /// <param name="dbPath">The database file. Defaults to <see cref="CcStorage.GatewayDb"/>.</param>
    public GatewayDatabase(ITenantContext tenant, string? dbPath = null)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _path = string.IsNullOrWhiteSpace(dbPath) ? CcStorage.GatewayDb() : dbPath!;

        FileLog.Write($"[GatewayDatabase] Open: path={_path}");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Foreign Keys=True enforces foreign keys on every pooled connection (it is per-connection, so
            // it rides on the connection string rather than a one-off pragma).
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                ForeignKeys = true,
            }.ToString();

            var services = new ServiceCollection();
            services.AddPooledDbContextFactory<GatewayDbContext>(o => o.UseSqlite(connectionString));
            _provider = services.BuildServiceProvider();
            _factory = _provider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();

            using (var ctx = _factory.CreateDbContext())
            {
                // Apply the EF migration set. This creates the schema on a fresh file and migrates an older
                // one forward - the EF equivalent of the stats database's PRAGMA user_version steps.
                ctx.Database.Migrate();
                // Write-ahead logging: a reader never blocks the single writer. journal_mode=WAL is persisted
                // in the database header, so setting it once here applies to every future pooled connection.
                ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }

            FileLog.Write($"[GatewayDatabase] Open: ready, path={_path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDatabase] Open FAILED: path={_path}: {ex.Message}");
            throw new InvalidOperationException(
                $"The Gateway database at '{_path}' could not be opened or migrated: {ex.Message}. " +
                "The Gateway will not fall back to the old JSON stores. Fix the database file " +
                "(or move it aside to start a fresh one) and restart the Gateway.", ex);
        }
    }

    /// <summary>
    /// A fresh context scoped to the current tenant. Resolves the ambient tenant, fails loud on an invalid
    /// one (so a default(TenantId) never reaches a query - mirroring PushedSessionStore.DirectorsFor), and
    /// stamps the context so the global query filter and every write scope to it. The caller disposes it
    /// (it returns to the pool).
    /// </summary>
    public GatewayDbContext CreateContext()
    {
        var tenant = _tenant.Current;
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        var ctx = _factory.CreateDbContext();
        ctx.ActiveTenant = tenant.Value;
        return ctx;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
        // Release the underlying SQLite connections so a test can delete the database file.
        SqliteConnection.ClearAllPools();
        FileLog.Write($"[GatewayDatabase] Dispose: closed {_path}");
    }
}
