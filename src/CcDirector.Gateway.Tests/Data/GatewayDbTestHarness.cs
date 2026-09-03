using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Test harness for the EF data layer: a throwaway on-disk SQLite database under the system temp directory,
/// opened over a real file (the <see cref="global::CcDirector.Gateway.Stats.GatewayStatsDatabase"/> test pattern - real file, and
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

    /// <summary>
    /// THE MIGRATED SCHEMA, BUILT ONCE PER TEST PROCESS AND THEN COPIED.
    ///
    /// Constructing a GatewayDatabase over an empty file runs the whole migration set, and that was the
    /// single largest cost in this assembly: a database-backed test took about 355 milliseconds against
    /// 0.45 for a pure one - roughly eight hundred times slower - and well over half the suite is
    /// database-backed. It was not sleeping and it was not short of cores; it was rebuilding the same
    /// schema from scratch, once per test, hundreds of times per run.
    ///
    /// Migrating once and copying the resulting file is behaviour-preserving rather than a shortcut: the
    /// copy is produced by the SAME constructor over the same migration set, so every test still opens the
    /// real schema through the real code path. EF then finds no pending migrations and skips Migrate(),
    /// which the database already handles as a first-class case. What is removed is the repetition, not a
    /// step.
    /// </summary>
    /// The template is held as BYTES, not as a path, and that detail is load-bearing. The first version
    /// copied a file and called SqliteConnection.ClearAllPools() to release it - and ClearAllPools is
    /// PROCESS-GLOBAL. Built lazily on first use, it fired while other tests were already running and
    /// yanked THEIR pooled connections out from under them: every full run failed exactly one database
    /// test, a different one each time, all passing in isolation. Reading the bytes once under a sharing
    /// handle needs no pool clear at all, so nothing outside this type is disturbed - and writing from
    /// memory is faster than copying a file besides.
    private static readonly Lazy<byte[]> MigratedTemplate = new(BuildTemplate, isThreadSafe: true);

    private static byte[] BuildTemplate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-gateway-db-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "template.db");
        // Construct and dispose: the constructor is what applies the migrations. Taken under the gate,
        // because the constructor reads the process-global provider selection and one test in the suite
        // has to blank it - see GatewayDbEnvironmentGate.
        GatewayDbEnvironmentGate.WhileTheConfigurationIsStable(() =>
        {
            using (var db = new GatewayDatabase(new SingleTenantContext(), path)) { }
            return true;
        });
        // Read it back with full sharing rather than clearing the global connection pool.
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort; the bytes are already held */ }
        return bytes;
    }

    /// <summary>Open the shared database with the given tenant (defaults to the local single tenant).</summary>
    public GatewayDatabase Open(ITenantContext? tenant = null)
    {
        Directory.CreateDirectory(_dir);
        // Seed from the migrated template on FIRST open only. A second Open() over the same path is a test
        // deliberately simulating a Gateway restart, and must find the database it just wrote - not a fresh
        // one - so the copy is conditional on the file not already existing.
        if (!File.Exists(DbPath))
        {
            File.WriteAllBytes(DbPath, MigratedTemplate.Value);
        }
        var db = GatewayDbEnvironmentGate.WhileTheConfigurationIsStable(
            () => new GatewayDatabase(tenant ?? new SingleTenantContext(), DbPath));
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
