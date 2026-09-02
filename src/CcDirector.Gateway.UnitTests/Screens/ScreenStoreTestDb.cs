using CcDirector.Gateway.Data;
using CcDirector.Gateway.Screens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// A throwaway SQLite database for the terminal-screen store's tests, with its tables created from the
/// mapped MODEL rather than from the migration chain.
///
/// WHY. The Terminal Rules mission's <c>session_screens</c> migration is not written: the fleet-wide EF
/// migration slot is held by another branch, and two migrations cut from one model snapshot is the
/// collision that rule exists to prevent (see
/// <c>docs/missions/terminal-rules-2026-09-02/rulings/r2-migration-slot.md</c>). A real
/// <see cref="GatewayDatabase"/> builds its schema with <c>Database.Migrate()</c> on both providers, so
/// until the migration lands it cannot produce a database containing the table - and the store's LOGIC
/// would go untested for as long as the slot is held.
///
/// <c>StatsConcurrencyTestDb</c> in the Gateway.Tests project established this instrument for exactly
/// this situation, and its comment says why it is sound: the tables are built from the same mapped model
/// the store's own statements are generated from, so a disagreement between the model and the statements
/// still fails here, which is the property these tests need.
///
/// THE LIMIT, WHICH TRAVELS WITH EVERY RESULT THIS PRODUCES. The real Gateway builds its tables from the
/// MIGRATION FILE, and that is a different generator. Tests run against this database are
/// <b>proven against the mapped model, not the migrated schema</b>. They say nothing about whether the
/// migration produces the same shape. When the slot frees, a pending-model-changes check must assert the
/// two agree - a disagreement VOIDS these results rather than merely dating them - every row must be
/// re-run against a migrated database, and this class must then be DELETED rather than left as a second,
/// easier path that outlives its reason.
///
/// A SECOND LIMIT, and it is not about the schema. Tests built on this seed the store BY HAND. They do
/// not drive <c>TurnReviewLogger</c> to <c>GatewayScreenSink</c> to the hub to the store, so they would
/// all pass if the push were wired to nothing at all. They prove the store and the reader behave
/// correctly WHEN HANDED a screen, and no more than that.
/// </summary>
internal sealed class ScreenStoreTestDb : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<GatewayDbContext> _factory;
    private readonly string _path;

    public ScreenStoreTestDb()
    {
        _path = Path.Combine(Path.GetTempPath(), "cc-screens-db-" + Guid.NewGuid().ToString("N") + ".db");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            ForeignKeys = true,
        }.ToString();

        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayDbContext>(o => o.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();

        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
        // Write-ahead logging, matching how the Gateway opens its own SQLite databases, so a reader never
        // blocks the single writer and the tests spend their time on what they are measuring.
        ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// A context source stamped for ONE tenant, which is exactly what <see cref="GatewayDatabase"/> does
    /// for a request. Two of these over the same database is how the tenant-partition proof is driven: one
    /// account writes and reads through its own, the other through its own, against one set of tables.
    /// </summary>
    public Func<GatewayDbContext> ContextFor(string tenantId) => () =>
    {
        var ctx = _factory.CreateDbContext();
        ctx.ActiveTenant = tenantId;
        return ctx;
    };

    /// <summary>A store scoped to one tenant, over this database.</summary>
    public SessionScreenStore StoreFor(string tenantId) => new(ContextFor(tenantId));

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        // Best effort. A leftover temp file costs nothing, but a run that leaves hundreds behind is its
        // own small problem.
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
    }
}
