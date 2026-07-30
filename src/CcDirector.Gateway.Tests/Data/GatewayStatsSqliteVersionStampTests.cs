using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
