using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// MECHANICAL ENFORCEMENT of the rule that every migration added to the SQLite statistics chain must move
/// <c>PRAGMA user_version</c>.
///
/// The rule exists because the shipped hand-rolled <see cref="GatewayStatsDatabase"/> reads that stamp to
/// decide whether it understands a file, and refuses - loudly and correctly - to open one written by a newer
/// build. That refusal is the safety net for a DESKTOP ROLLBACK, which is a thing that actually happens now
/// that desktop releases are authorised. If someone adds a migration in six months and the stamp does not
/// move, an older build meeting the newer file reads a version it thinks it already understands, runs its own
/// version 1 through 5 steps against tables that already exist, and dies on a duplicate ALTER TABLE. The
/// failure would not be a red test in this repository - it would be a crash on a real user's statistics
/// database, arriving by a route nobody was looking at.
///
/// So it is not left to memory. A rule somebody has to remember is the shape this mission has already
/// rejected more than once; this test is the rule with a mechanism behind it.
///
/// The expected stamp is DERIVED FROM THE CHAIN, never held as a constant. A constant is the same forgettable
/// rule wearing a different hat: it has to be maintained by the same person who would have had to remember
/// the stamp.
/// </summary>
public sealed class GatewayStatsSqliteVersionStampTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsSqliteVersionStampTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// The number of schema versions that existed BEFORE this chain began - the versions the baseline
    /// migration collapses into itself.
    ///
    /// THIS IS WHY THE NUMBER IS 4, AND IT IS NOT AN OFF-BY-ONE. The hand-rolled store went through schema
    /// versions 1, 2, 3, 4 and 5 as five separate steps. The Entity Framework baseline reproduces the END of
    /// that sequence in ONE migration, because a store on disk is only ever at version 5 - versions 1 through
    /// 4 are history that no live file is sitting in. So one migration corresponds to five schema versions:
    /// 4 already-collapsed versions, plus one per migration in the chain. Today that is 4 + 1 = 5, which is
    /// exactly what a real version 5 file reports.
    ///
    /// Do not "correct" this to 5 or to 0. Either would break the relationship the rest of this test relies
    /// on, and the correction would look reasonable and be silent.
    /// </summary>
    private const int SchemaVersionsCollapsedIntoTheBaseline = 4;

    private GatewayStatsDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _path }.ToString())
            .Options;
        return new GatewayStatsDbContext(options);
    }

    private int UserVersion()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void EveryMigrationInTheSqliteChainMovesTheSchemaVersionStamp()
    {
        int migrationCount;
        using (var context = OpenContext())
        {
            migrationCount = context.Database.GetMigrations().Count();
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();

        // Refuse a check that could not fail: with no migrations in the chain the arithmetic degenerates and
        // this would be asserting something about a database nothing built.
        Assert.True(migrationCount >= 1,
            "The SQLite statistics chain reports no migrations at all, so this check has nothing to verify " +
            "and its result is meaningless. The migrations assembly is missing from the build.");

        var expected = SchemaVersionsCollapsedIntoTheBaseline + migrationCount;
        var actual = UserVersion();

        Assert.True(actual == expected,
            $"The SQLite statistics chain has {migrationCount} migration(s), so a freshly migrated store " +
            $"should stamp PRAGMA user_version = {expected}, but it stamps {actual}. " +
            "A MIGRATION WAS ADDED TO THE CHAIN WITHOUT MOVING THE VERSION STAMP. Every migration that " +
            "changes this schema must raise the stamp by one, in its own Up(), with a matching reset in its " +
            "Down(). The stamp is what an OLDER build of DevThrottle reads to decide whether it understands " +
            "a statistics file: leave it behind and a user who rolls their desktop build back gets that " +
            "build running its own version 1 to 5 steps against tables that already exist, instead of the " +
            "clean refusal it would otherwise give. " +
            $"(The expected value is {SchemaVersionsCollapsedIntoTheBaseline} plus the migration count, " +
            $"because the baseline migration collapses schema versions 1 to 5 into one migration.)");
    }

    /// <summary>
    /// EVERY MIGRATION INDIVIDUALLY, UP AND DOWN - because the end-state sum above cannot see two errors that
    /// cancel.
    ///
    /// A review defeated the sum twice, the same way both times: it added one migration that did not move the
    /// stamp and another that jumped straight to the correct final value, and the total still landed on
    /// 4 + count. The mechanism was wrong, not the threshold. A sum over a sequence cannot detect a
    /// compensating pair, so this stops checking arithmetic on the end state and walks the chain.
    ///
    /// Each migration is applied ON ITS OWN, by migrating to it as a target, and must move the stamp by
    /// EXACTLY ONE. A migration that moves it by nothing fails at its own step; a migration that jumps to the
    /// final value fails at its own step too, because it overshoots before any later migration can compensate.
    ///
    /// Then the chain is walked back DOWN, which the sum never exercised at all. An empty Down() was the
    /// second hole in the same test, and the failure message has always promised "a matching reset in its
    /// Down()" - a promise nothing checked.
    /// </summary>
    [Fact]
    public void EveryMigrationMovesTheStampByExactlyOne_UpAndBackDown()
    {
        using var context = OpenContext();
        var migrations = context.Database.GetMigrations().ToList();

        Assert.True(migrations.Count >= 1,
            "The SQLite statistics chain reports no migrations, so this check has nothing to walk.");

        var migrator = context.GetService<IMigrator>();

        // UP, one at a time.
        for (var i = 0; i < migrations.Count; i++)
        {
            migrator.Migrate(migrations[i]);

            var expected = SchemaVersionsCollapsedIntoTheBaseline + i + 1;
            var actual = UserVersion(context);

            Assert.True(actual == expected,
                $"After applying migration '{migrations[i]}' - number {i + 1} of {migrations.Count} in the " +
                $"chain - PRAGMA user_version should be {expected} but is {actual}. EVERY MIGRATION MUST " +
                "MOVE THE STAMP BY EXACTLY ONE, in its own Up(). Moving it by nothing leaves an older build " +
                "unable to tell that a file is newer than itself; moving it by more than one claims schema " +
                "versions that never existed, and lets a later migration that moves it by nothing hide " +
                "behind this one in any end-state total.");
        }

        // DOWN, one at a time, back to nothing.
        for (var i = migrations.Count - 1; i >= 0; i--)
        {
            var target = i == 0 ? Migration.InitialDatabase : migrations[i - 1];
            migrator.Migrate(target);

            var expected = i == 0 ? 0 : SchemaVersionsCollapsedIntoTheBaseline + i;
            var actual = UserVersion(context);

            Assert.True(actual == expected,
                $"After reverting migration '{migrations[i]}' PRAGMA user_version should be {expected} but " +
                $"is {actual}. EVERY MIGRATION MUST RESET THE STAMP IN ITS Down(), matching what its Up() " +
                "set. A Down() that leaves the stamp where it was makes the store claim a schema version it " +
                "no longer has, which is the same lie as never stamping it - and reverting the baseline must " +
                "return the file to an unstamped 0, because that is what a store which has never held this " +
                "schema looks like.");
        }
    }

    /// <summary>Read the stamp on the connection the migrator is using, so it is the same file and there is
    /// no pool to clear between steps.</summary>
    private static int UserVersion(GatewayStatsDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) context.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// The stamp a fresh chain-built store carries is the SAME one the shipped hand-rolled code writes. This
    /// is the other end of the same rule: the derived arithmetic above is only meaningful if it lands on the
    /// version real files in the field actually carry.
    /// </summary>
    [Fact]
    public void TheStampAFreshChainBuiltStoreCarriesIsTheOneTheShippedCodeWrites()
    {
        using (var context = OpenContext())
        {
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(GatewayStatsDatabase.SchemaVersion, UserVersion());
    }
}
