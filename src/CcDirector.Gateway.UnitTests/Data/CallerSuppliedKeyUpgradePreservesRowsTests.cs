using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Proves the caller-supplied-key composite migration is a SAFE UPGRADE for a database that already holds
/// rows - by actually running it, on a real database, with real rows in it.
///
/// This exists because the claim it checks cannot be established by reading the migration. The generated
/// operations are three <c>DropPrimaryKey</c> / <c>AddPrimaryKey</c> pairs, and on SQLite a primary key
/// cannot be altered in place at all: the provider silently turns each pair into a TABLE REBUILD - create a
/// new table with the new key, copy the rows across, drop the original, rename. Whether the copy carries
/// every row and every column value is a property of that generated rebuild, not of the three lines anyone
/// reads in the migration file, so reasoning about the operations proves nothing about the data. The only
/// evidence that counts is a database that had rows before and has the same rows after.
///
/// So the test walks the real upgrade path a deployed Gateway will walk:
///   1. Migrate a fresh database to <c>CompositeTenantKeys</c> - the migration immediately BEFORE this
///      change - so the schema is exactly the pre-change one, with single-column primary keys.
///   2. Insert rows through raw SQL, not through EF. EF would map them with today's model and today's
///      composite keys; raw SQL writes them the way a database in the field actually holds them.
///   3. Migrate the rest of the way, applying the change under test.
///   4. Assert every row is still present, with every column value unchanged - and that the key really did
///      become composite, so a green result cannot come from the migration having quietly not run.
///
/// Postgres runs the same three operations against a provider that CAN alter a primary key in place, so it
/// never rebuilds a table and has strictly less to lose. It is covered by the gated live-Postgres proof
/// tests rather than here; this test needs no external service and always runs.
/// </summary>
public sealed class CallerSuppliedKeyUpgradePreservesRowsTests : IDisposable
{
    /// <summary>The migration immediately before the change under test - the "pre-change database".</summary>
    private const string MigrationBeforeTheChange = "CompositeTenantKeys";

    /// <summary>The change under test.</summary>
    private const string MigrationUnderTest = "CallerSuppliedKeysScopedByTenant";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-gateway-upgrade-tests-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "gateway.db");

    private string ConnectionString => "Data Source=" + DbPath;

    public CallerSuppliedKeyUpgradePreservesRowsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort - the OS may hold the file briefly after the pool clear */ }
    }

    private GatewayDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        builder.UseSqlite(ConnectionString);
        return new GatewayDbContext(builder.Options);
    }

    private void MigrateTo(string? target)
    {
        using var ctx = NewContext();
        var migrator = AccessorExtensions.GetService<IMigrator>(ctx);
        migrator.Migrate(target);
    }

    /// <summary>
    /// The assumption this whole proof rests on, asserted instead of assumed: that
    /// <see cref="MigrationBeforeTheChange"/> is the migration IMMEDIATELY before
    /// <see cref="MigrationUnderTest"/>, so migrating to it produces the exact pre-change schema and the
    /// second migrate applies THIS change and nothing else.
    ///
    /// It is checked because breaking it is SILENT. If another branch lands a migration between those two,
    /// nothing in this file changes, a range-diff of this branch shows everything identical, and every
    /// assertion below still passes - the rows are still preserved, the key still ends up composite. But the
    /// test would no longer be isolating this change: it would be carrying rows across someone else's
    /// migration as well and reporting the result as evidence about this one. Text-identity is not
    /// meaning-identity, and a clean rebase is not a guarantee that a proof still proves what it did.
    /// </summary>
    private void AssertThisProofStillIsolatesTheMigrationUnderTest()
    {
        using var ctx = NewContext();
        var all = ctx.Database.GetMigrations().ToList();

        var underTest = all.FindIndex(m => m.EndsWith(MigrationUnderTest, StringComparison.Ordinal));
        Assert.True(underTest >= 0,
            $"'{MigrationUnderTest}' is not in the migration set at all - this proof is pointed at a " +
            "migration that no longer exists.");
        Assert.True(underTest > 0, $"'{MigrationUnderTest}' is now the FIRST migration, so there is no " +
            "pre-change schema to upgrade from.");

        Assert.True(all[underTest - 1].EndsWith(MigrationBeforeTheChange, StringComparison.Ordinal),
            $"This proof assumes '{MigrationBeforeTheChange}' is the migration immediately before " +
            $"'{MigrationUnderTest}', but the migration before it is now '{all[underTest - 1]}'. Another " +
            "change has landed in between, so migrating to the named one no longer produces the pre-change " +
            "schema and this test would be carrying rows across that other migration too while reporting " +
            "the result as evidence about this one. Re-point the constant and re-read the fixture against " +
            "the new predecessor - do not simply update the name.");
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private List<Dictionary<string, object?>> Query(string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The columns of a table's PRIMARY KEY, in key order, straight from SQLite's own catalogue.</summary>
    private List<string> PrimaryKeyColumns(string table)
        => Query($"SELECT name, pk FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk")
            .Select(r => (string)r["name"]!)
            .ToList();

    [Fact]
    public void UpgradingADatabaseThatAlreadyHasRows_KeepsEveryRow_AndMakesTheKeyComposite()
    {
        // 0. The proof still isolates THIS migration - see the method for why this cannot be assumed.
        AssertThisProofStillIsolatesTheMigrationUnderTest();

        // 1. A database at exactly the pre-change schema.
        MigrateTo(MigrationBeforeTheChange);

        // The pre-change keys really are the single-column ones - otherwise step 3 would prove nothing,
        // because the schema would already have been what we are trying to migrate TO.
        Assert.Equal(new[] { "SessionId" }, PrimaryKeyColumns("snoozes"));
        Assert.Equal(new[] { "SessionId" }, PrimaryKeyColumns("session_spend"));
        Assert.Equal(new[] { "Endpoint" }, PrimaryKeyColumns("push_subscriptions"));

        // 2. Rows written the way a database in the field holds them - including two tenants, which is the
        //    state the whole change exists to make representable.
        Execute("""
            INSERT INTO snoozes (SessionId, tenant_id, DirectorId, OwnerTurnBaselineUtc, PendingMinutes, SnoozeUntilUtc)
            VALUES ('session-alpha', 'tenant-one', 'director-a', '2026-07-20 09:00:00',   15, '2026-07-20 10:00:00'),
                   ('session-beta',  'tenant-two', 'director-b', NULL,                  NULL, '2026-07-21 11:30:00');

            INSERT INTO session_spend
                (SessionId, tenant_id, AgentKind, BillingMode, CacheCreationTokens, CacheReadTokens,
                 FirstObservedUtc, InputTokens, LastObservedUtc, MeteredCostMicros, Model, OutputTokens,
                 RepoPath, TokensCaptured)
            VALUES ('session-alpha', 'tenant-one', 'claude', 'subscription', 11, 22,
                    '2026-07-20 08:00:00', 33, '2026-07-20 09:00:00', 1234567, 'opus', 44,
                    'D:\ReposFred\devthrottle', 1),
                   ('session-gamma', 'tenant-two', 'codex',  'metered',       0,  0,
                    '2026-07-20 09:15:00',  5, '2026-07-20 09:30:00',    NULL, NULL,   6,
                    NULL, 0);

            INSERT INTO push_subscriptions (Endpoint, tenant_id, Auth, CreatedAtUtc, P256dh)
            VALUES ('https://push.example/one', 'tenant-one', 'auth-one', '2026-07-19 08:00:00', 'key-one'),
                   ('https://push.example/two', 'tenant-two', 'auth-two', '2026-07-19 08:05:00', 'key-two');
            """);

        var snoozesBefore = Query("SELECT * FROM snoozes ORDER BY SessionId");
        var spendBefore = Query("SELECT * FROM session_spend ORDER BY SessionId");
        var pushBefore = Query("SELECT * FROM push_subscriptions ORDER BY Endpoint");
        Assert.Equal(2, snoozesBefore.Count);
        Assert.Equal(2, spendBefore.Count);
        Assert.Equal(2, pushBefore.Count);

        // 3. The upgrade under test - the SQLite table rebuild actually executes here.
        MigrateTo(target: null);

        // 4a. The migration genuinely ran - by name, out of the migrations history table, and by its effect
        //     on the schema. Without both, a migration that had silently done nothing would sail through the
        //     row assertions below: the rows would be intact precisely because nothing had touched them.
        Assert.Contains(
            Query("SELECT MigrationId FROM __EFMigrationsHistory").Select(r => (string)r["MigrationId"]!),
            id => id.EndsWith(MigrationUnderTest, StringComparison.Ordinal));

        Assert.Equal(new[] { "tenant_id", "SessionId" }, PrimaryKeyColumns("snoozes"));
        Assert.Equal(new[] { "tenant_id", "SessionId" }, PrimaryKeyColumns("session_spend"));
        Assert.Equal(new[] { "tenant_id", "Endpoint" }, PrimaryKeyColumns("push_subscriptions"));

        // 4b. Every row survived, with every column value byte-for-byte what it was before the upgrade.
        AssertRowsUnchanged(snoozesBefore, Query("SELECT * FROM snoozes ORDER BY SessionId"), "snoozes");
        AssertRowsUnchanged(spendBefore, Query("SELECT * FROM session_spend ORDER BY SessionId"), "session_spend");
        AssertRowsUnchanged(pushBefore, Query("SELECT * FROM push_subscriptions ORDER BY Endpoint"),
            "push_subscriptions");
    }

    /// <summary>
    /// Proves the upgrade also DELIVERS what it is for: after it, the two tenants can each hold the same
    /// session id, which is precisely what the old single-column key made impossible. Before the change this
    /// insert fails on the primary key - that failure being both the cross-tenant squat and the existence
    /// oracle the change removes.
    /// </summary>
    [Fact]
    public void AfterTheUpgrade_TwoTenantsCanHoldTheSameCallerSuppliedIdentifier()
    {
        MigrateTo(MigrationBeforeTheChange);
        Execute("""
            INSERT INTO snoozes (SessionId, tenant_id, DirectorId, SnoozeUntilUtc)
            VALUES ('shared-session', 'tenant-one', 'director-first', '2026-07-20 10:00:00');
            """);

        // The pre-change schema refuses the second tenant outright - the defect, demonstrated.
        var squat = Assert.Throws<SqliteException>(() => Execute("""
            INSERT INTO snoozes (SessionId, tenant_id, DirectorId, SnoozeUntilUtc)
            VALUES ('shared-session', 'tenant-two', 'director-second', '2026-07-20 10:00:00');
            """));
        Assert.Contains("UNIQUE constraint failed", squat.Message, StringComparison.Ordinal);

        MigrateTo(target: null);

        // After the upgrade the same insert is accepted, and both rows coexist.
        Execute("""
            INSERT INTO snoozes (SessionId, tenant_id, DirectorId, SnoozeUntilUtc)
            VALUES ('shared-session', 'tenant-two', 'director-second', '2026-07-20 10:00:00');
            """);

        var rows = Query("SELECT tenant_id, DirectorId FROM snoozes WHERE SessionId = 'shared-session' ORDER BY tenant_id");
        Assert.Equal(2, rows.Count);
        Assert.Equal("tenant-one", rows[0]["tenant_id"]);
        Assert.Equal("director-first", rows[0]["DirectorId"]);
        Assert.Equal("tenant-two", rows[1]["tenant_id"]);
        Assert.Equal("director-second", rows[1]["DirectorId"]);
    }

    private static void AssertRowsUnchanged(
        List<Dictionary<string, object?>> before,
        List<Dictionary<string, object?>> after,
        string table)
    {
        Assert.True(before.Count == after.Count,
            $"{table}: the upgrade changed the row count from {before.Count} to {after.Count} - rows were " +
            "lost or duplicated by the table rebuild.");

        for (var i = 0; i < before.Count; i++)
        {
            Assert.True(before[i].Count == after[i].Count,
                $"{table} row {i}: the upgrade changed the column count from {before[i].Count} to " +
                $"{after[i].Count}.");

            foreach (var (column, value) in before[i])
            {
                Assert.True(after[i].ContainsKey(column),
                    $"{table} row {i}: column '{column}' is gone after the upgrade.");
                Assert.True(Equals(value, after[i][column]),
                    $"{table} row {i}: column '{column}' changed across the upgrade - was '{value}', " +
                    $"now '{after[i][column]}'.");
            }
        }
    }
}
