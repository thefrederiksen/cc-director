using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
    /// <summary>The environment variable that, when set to a non-whitespace connection string, switches the
    /// Gateway database from the local SQLite file onto PostgreSQL (single-tenant hosted Gateway). Unset means
    /// the local install: the SQLite file behavior is unchanged.</summary>
    public const string PostgresConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";

    private readonly string _path;
    private readonly bool _usePostgres;
    private readonly ITenantContext _tenant;
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<GatewayDbContext> _factory;
    private bool _disposed;

    /// <summary>The database file path (SQLite), for logging. On the Postgres path this is NOT a file - it
    /// holds a credential-redacted description of the connection target instead.</summary>
    public string Path => _path;

    /// <param name="tenant">The ambient tenant context. On the local install this is
    /// <see cref="SingleTenantContext"/> and every row is the "local" tenant.</param>
    /// <param name="dbPath">The SQLite database file. Defaults to <see cref="CcStorage.GatewayDb"/>. Ignored
    /// when <see cref="PostgresConnectionEnvVar"/> is set (the Postgres path never touches a file).</param>
    public GatewayDatabase(ITenantContext tenant, string? dbPath = null)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));

        // Provider selection is config-driven and fail-loud: when CC_GATEWAY_DB_CONNECTION carries a
        // connection string the Gateway runs on Postgres; otherwise it is the local SQLite file. There is NO
        // fallback between the two - a configured-but-broken Postgres throws below, it never silently reverts
        // to SQLite (which would hide a real cloud misconfiguration behind a healthy-looking local database).
        //
        // The three cases are distinct and blank is NOT the same as unset: the variable being UNSET (null)
        // means "local install, use SQLite", but the variable being SET to a blank/whitespace value is a
        // misconfiguration - the operator meant to point at Postgres and left the value empty - so it fails
        // loud here rather than silently picking SQLite (which would be exactly the hidden fallback the
        // no-fallback rule forbids).
        var pgConn = Environment.GetEnvironmentVariable(PostgresConnectionEnvVar);
        if (pgConn is not null && string.IsNullOrWhiteSpace(pgConn))
            throw new InvalidOperationException(
                PostgresConnectionEnvVar + " is set but blank; set a real PostgreSQL connection string or " +
                "unset it to use local SQLite. The Gateway will not fall back.");

        _usePostgres = pgConn is not null;

        if (_usePostgres)
        {
            // The _path field is SQLite-specific; on the Postgres path it holds a redacted target (host +
            // database only) for logging, never the file path and never the credentials.
            _path = RedactConnectionTarget(pgConn!);

            FileLog.Write($"[GatewayDatabase] Open: provider=Postgres target={_path}");

            // Build the provider into a LOCAL first and only publish it to the readonly fields after Migrate()
            // succeeds. If Migrate throws, the constructor never completes, so Dispose() can never run - the
            // catch disposes the local provider itself, otherwise its pooled connections would leak.
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                services.AddPooledDbContextFactory<GatewayDbContext>(o => o.UseNpgsql(pgConn, npg =>
                {
                    npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
                }));
                provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();

                using (var ctx = factory.CreateDbContext())
                {
                    // Apply the Postgres migration set (its own assembly + gateway-schema history table). No
                    // PRAGMA journal_mode here - WAL is a SQLite-only setting and Postgres has its own WAL.
                    ctx.Database.Migrate();
                }

                _provider = provider;
                _factory = factory;

                FileLog.Write($"[GatewayDatabase] Open: ready, provider=Postgres target={_path}");
            }
            catch (Exception ex)
            {
                provider?.Dispose();
                // The provider's exception message can carry the raw connection string (a malformed one is
                // echoed back by the parser), so it is NEVER interpolated into the log or the thrown message.
                // The redacted target plus the exception TYPE name is enough to diagnose; the full exception is
                // preserved as the InnerException without us stringifying it anywhere.
                FileLog.Write($"[GatewayDatabase] Open FAILED: provider=Postgres target={_path}: {ex.GetType().Name}");
                throw new InvalidOperationException(
                    $"The Gateway PostgreSQL database ('{_path}') could not be opened or migrated ({ex.GetType().Name}). " +
                    "PostgreSQL is configured via " + PostgresConnectionEnvVar + ", so the Gateway will NOT " +
                    "fall back to SQLite or to the old JSON stores. Fix the connection string, the server, or " +
                    "the schema and restart the Gateway.", ex);
            }
            return;
        }

        _path = string.IsNullOrWhiteSpace(dbPath) ? CcStorage.GatewayDb() : dbPath!;

        FileLog.Write($"[GatewayDatabase] Open: provider=Sqlite path={_path}");
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
                // This is a SQLite-only pragma and never runs on the Postgres path above.
                ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }

            FileLog.Write($"[GatewayDatabase] Open: ready, provider=Sqlite path={_path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDatabase] Open FAILED: provider=Sqlite path={_path}: {ex.Message}");
            throw new InvalidOperationException(
                $"The Gateway SQLite database at '{_path}' could not be opened or migrated: {ex.Message}. " +
                "The Gateway will not fall back to the old JSON stores. Fix the database file " +
                "(or move it aside to start a fresh one) and restart the Gateway.", ex);
        }
    }

    /// <summary>
    /// Build a credential-free description of a Postgres connection for logging: host and database ONLY, never
    /// the username or password. The connection string is parsed with the Npgsql builder (which correctly
    /// handles quoted values and passwords containing ';' or '='); only Host and Database are read back. Any
    /// parse trouble degrades to a fixed literal, and the builder's own ToString() is never called, so a
    /// password can never reach the log through this method.
    /// </summary>
    /// <remarks>Internal (not private) only so the credential-redaction guarantee can be unit-tested directly
    /// via InternalsVisibleTo; it is not part of the public surface.</remarks>
    internal static string RedactConnectionTarget(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"postgres host={builder.Host} database={builder.Database}";
        }
        catch
        {
            return "postgres (target redacted)";
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
        // Release the underlying SQLite connections so a test can delete the database file. SQLite-only:
        // the Postgres path has no local file to release and Npgsql pooling is managed by the provider.
        if (!_usePostgres)
            SqliteConnection.ClearAllPools();
        FileLog.Write($"[GatewayDatabase] Dispose: closed {_path}");
    }
}
