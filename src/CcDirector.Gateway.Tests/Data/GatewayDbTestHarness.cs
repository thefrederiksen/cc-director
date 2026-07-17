using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Test harness for the EF data layer: a throwaway on-disk SQLite database under the system temp directory,
/// opened over a real file (the <see cref="Stats.GatewayStatsDatabase"/> test pattern - real file, and
/// <c>SqliteConnection.ClearAllPools()</c> on dispose so the file is released). Every opened
/// <see cref="GatewayDatabase"/> is tracked and disposed, and the whole directory is removed on teardown.
///
/// <see cref="Open"/> a database (default tenant <see cref="TenantId.Local"/>); call it again over the SAME
/// file to simulate a Gateway restart. Use <see cref="LegacyPath"/> for a legacy JSON import path in the
/// same directory.
/// </summary>
public sealed class GatewayDbTestHarness : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-gateway-db-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<GatewayDatabase> _opened = new();

    /// <summary>The single database file every <see cref="Open"/> in this harness shares.</summary>
    public string DbPath => Path.Combine(_dir, "gateway.db");

    /// <summary>A path in this harness's directory (for a legacy JSON import file, or any test file).</summary>
    public string LegacyPath(string name) => Path.Combine(_dir, name);

    /// <summary>Open the shared database with the given tenant (defaults to the local single tenant).</summary>
    public GatewayDatabase Open(ITenantContext? tenant = null)
    {
        Directory.CreateDirectory(_dir);
        var db = new GatewayDatabase(tenant ?? new SingleTenantContext(), DbPath);
        _opened.Add(db);
        return db;
    }

    public void Dispose()
    {
        foreach (var db in _opened)
        {
            try { db.Dispose(); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort - the OS may hold the file briefly after pool clear */ }
    }
}

/// <summary>A test double for <see cref="ITenantContext"/> that always returns a fixed tenant, so tenant
/// isolation (the global query filter) can be exercised with two distinct tenants over one database.</summary>
public sealed class FixedTenantContext : ITenantContext
{
    public FixedTenantContext(TenantId tenant) => Current = tenant;
    public TenantId Current { get; }
}
