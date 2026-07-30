using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A throwaway SQLite statistics database for the concurrency-store tests, and the ability to open SEVERAL
/// independent context factories against the same file.
///
/// The several factories are the point, not a convenience: each one is its own pooled service provider with
/// its own connections, which is the closest a single test process gets to the two Gateway CONTAINERS that a
/// slot swap runs against one store. A test that drove one factory could never observe a lost update, so it
/// could never prove the upserts are what prevents one.
///
/// The schema is created with EnsureCreated rather than a migration. Worker 2 owns the migration chain for
/// the whole sixteen-plus-three-table context; a migration scaffolded from this three-table slice would have
/// to be discarded the moment the rest arrives. EnsureCreated builds the tables from the same mapped model
/// the store's statements are generated from, so a mismatch between the model and the statements still fails
/// here - which is the property these tests need from it.
/// </summary>
internal sealed class StatsConcurrencyTestDb : IDisposable
{
    private readonly List<ServiceProvider> _providers = new();

    public string Path { get; }

    public StatsConcurrencyTestDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-conc-db-" + Guid.NewGuid().ToString("N") + ".db");

        using var ctx = NewFactory().CreateDbContext();
        ctx.Database.EnsureCreated();
        // Write-ahead logging so a reader never blocks the single writer, matching how the Gateway opens its
        // SQLite databases. Without it the multi-container tests spend their time on lock contention rather
        // than on the thing they are measuring.
        ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>A fresh, independent context factory against the same database file - one more "container".</summary>
    public IDbContextFactory<GatewayStatsDbContext> NewFactory()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            // The SQLite busy timeout: a writer that finds the database locked retries for this long instead
            // of failing immediately. Two containers writing the same store is the normal case here.
            DefaultTimeout = 30,
        }.ToString();

        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayStatsDbContext>(o => o.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        // Release the pooled SQLite handles so the file can actually be deleted on Windows.
        SqliteConnection.ClearAllPools();
        try { File.Delete(Path); } catch (IOException) { /* the file is a temp artifact; a locked one is not a test failure */ }
    }
}
