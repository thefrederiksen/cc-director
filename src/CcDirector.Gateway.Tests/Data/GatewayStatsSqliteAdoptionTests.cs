using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The self-host adoption step: an existing gateway-stats.db written by the hand-rolled
/// <see cref="GatewayStatsDatabase"/> is taken into the Entity Framework migration chain without losing a
/// row, and anything that is NOT such a store is refused with a named reason and a Gateway that keeps
/// running.
///
/// THE FIXTURE IS BUILT BY RUNNING THE OLD CODE, and that is the single point on which these tests are worth
/// something or worthless. Every version 5 file here is produced by constructing a real
/// <see cref="GatewayStatsDatabase"/> and letting it create and migrate the file - never by hand-writing what
/// a version 5 file is believed to look like, and never by generating one from the new model's understanding
/// of the old schema. A fixture synthesised from the new code's own understanding is a guard supplying its
/// own evidence: it would pass happily against a file shape that has never existed on any real machine, and
/// prove nothing about the installs this step exists to protect.
///
/// THE FAILURE IS WATCHED FIRST. <see cref="Migrate_Version5Store_WithoutAdoption_FailsOnTablesThatAlreadyExist"/>
/// runs the chain against such a file with NO adoption and pins the exact error it produces. Without it, the
/// adoption test could pass for a reason that has nothing to do with adoption - it is what proves the step is
/// load-bearing, and it turns red the day somebody deletes it.
/// </summary>
public sealed class GatewayStatsSqliteAdoptionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsSqliteAdoptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-adopt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Build a REAL version 5 statistics store at <see cref="_path"/> by running the shipped hand-rolled
    /// creation and migration code, then close it so the file is free. This is the state every self-host user
    /// who has ever opened the statistics page is in: sixteen tables, PRAGMA user_version 5, and no
    /// __EFMigrationsHistory table.
    /// </summary>
    private void BuildRealVersion5Store()
    {
        using (var db = new GatewayStatsDatabase(_path))
        {
            // Pin the fixture's own premises. If the shipped code ever stops producing this shape, these
            // tests must say so here rather than silently going on to prove something about a file that no
            // longer exists in the field.
            Assert.Equal(5, GatewayStatsDatabase.SchemaVersion);
            Assert.Equal(5, ScalarInt(db.Connection, "PRAGMA user_version"));
            Assert.Equal(0, ScalarInt(db.Connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'"));
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Write rows into the real version 5 store, so "no row was lost" is a claim with something behind it.
    /// Written through the OLD code's own connection, in the old code's own shape.
    ///
    /// EVERY VALUE IS DISTINGUISHABLE FROM EVERY OTHER, and that is the point rather than fussiness. This
    /// fixture's job is to catch a column mapped to the wrong column, and it can only do that if no two
    /// columns carry values a swap would leave looking identical. An earlier version of this row set
    /// <c>is_voice</c> and <c>wingman</c> BOTH to 0 and <c>model_id</c> and <c>checkout_id</c> BOTH to null -
    /// so a mapping that crossed either pair would have read back correct and every assertion below would
    /// have passed with the defect sitting in the model.
    ///
    /// So: the two booleans differ from each other, the two nullable surrogates carry different non-null
    /// numbers, the two counts differ, and every string differs. The second row is the one that keeps the
    /// nullable columns' NULL case covered, since row one no longer does.
    /// </summary>
    private void SeedDistinguishableStatDeltaRows()
    {
        using var db = new GatewayStatsDatabase(_path);

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T09', 'session-a', 'typed', 'terminal', 1, 3, 0, 7, 42, 11, 22, 'local')";
            command.ExecuteNonQuery();
        }

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T10', 'session-b', 'voice', 'mobile', 0, 5, 1, 13, 64, NULL, NULL, 'local')";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private GatewayStatsDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _path }.ToString())
            .Options;
        return new GatewayStatsDbContext(options);
    }

    // ---- the failure, watched on purpose --------------------------------------------------------------

    /// <summary>
    /// THE PROOF THAT THE ADOPTION STEP IS LOAD-BEARING. Run the migration chain against a real version 5
    /// store with no adoption and it fails, because the baseline migration creates sixteen tables that are
    /// already there. This is the exact breakage every self-host user would have hit.
    /// </summary>
    [Fact]
    public void Migrate_Version5Store_WithoutAdoption_FailsOnTablesThatAlreadyExist()
    {
        BuildRealVersion5Store();

        using var context = OpenContext();

        var ex = Assert.ThrowsAny<SqliteException>(() => context.Database.Migrate());

        // Pinned to the substance of the failure, not to a whole message: a table this store already owns
        // cannot be created again. Asserting only "it threw" would pass for a connection error too.
        //
        // It names stat_delta because the baseline creates the sixteen tables in the order schema version 5's
        // own migration steps introduced them, and stat_delta is the first. It dies there and never reaches
        // the fifteen behind it - which is the point.
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stat_delta", ex.Message, StringComparison.Ordinal);
    }

    // ---- and the same thing passing, because of adoption -----------------------------------------------

    [Fact]
    public void Adopt_RealVersion5Store_StampsBaselineAndLetsTheChainRun()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using var context = OpenContext();

        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.None, result.Reason);
        Assert.True(result.IsUsable);

        // The whole point: the chain now runs against the adopted file instead of failing on it.
        context.Database.Migrate();

        // Nothing is pending afterwards - the baseline is genuinely recorded as applied, not merely survived.
        Assert.Empty(context.Database.GetPendingMigrations());

        // The history table names the chain's own baseline, not something invented here.
        Assert.Equal(new[] { context.Database.GetMigrations().First() },
            context.Database.GetAppliedMigrations().ToArray());
    }

    [Fact]
    public void Adopt_RealVersion5Store_KeepsEveryRowItAlreadyHeld()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using var context = OpenContext();
        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(context).Outcome);
        context.Database.Migrate();

        // Read the pre-existing rows back THROUGH THE NEW MODEL. This is the port's real claim in one place:
        // the entity mapping lands on the columns the old code wrote, so the rows already on self-host disks
        // are readable without being copied or reshaped. It is currently the ONLY place the entity mapping
        // meets the real on-disk shape, so a wrong ToTable or HasColumnName throws here rather than passing
        // quietly.
        var rows = context.StatDeltas.OrderBy(r => r.HourUtc).ToList();
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("2026-07-30T09", first.HourUtc);
        Assert.Equal("session-a", first.SessionId);
        Assert.Equal("typed", first.Modality);
        Assert.Equal("terminal", first.Surface);
        Assert.True(first.IsVoice);
        Assert.Equal(3, first.RepoId);
        Assert.False(first.Wingman);
        Assert.Equal(7, first.Turns);
        Assert.Equal(42, first.Chars);
        Assert.Equal(11, first.ModelId);
        Assert.Equal(22, first.CheckoutId);
        Assert.Equal("local", first.Tenant);

        // The second row carries the opposite of every distinguishable value, and the NULL case for the two
        // nullable surrogates.
        var second = rows[1];
        Assert.Equal("2026-07-30T10", second.HourUtc);
        Assert.Equal("session-b", second.SessionId);
        Assert.Equal("voice", second.Modality);
        Assert.Equal("mobile", second.Surface);
        Assert.False(second.IsVoice);
        Assert.Equal(5, second.RepoId);
        Assert.True(second.Wingman);
        Assert.Equal(13, second.Turns);
        Assert.Equal(64, second.Chars);
        Assert.Null(second.ModelId);
        Assert.Null(second.CheckoutId);
        Assert.Equal("local", second.Tenant);
    }

    [Fact]
    public void Adopt_AlreadyAdoptedStore_IsTrackedAndDoesNothingTwice()
    {
        BuildRealVersion5Store();

        using (var first = OpenContext())
        {
            Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(first).Outcome);
            first.Database.Migrate();
        }

        SqliteConnection.ClearAllPools();

        // The next startup - and every startup after it - finds a tracked store and leaves it alone.
        using var second = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(second);

        Assert.Equal(StatsStoreAdoptionOutcome.AlreadyTracked, result.Outcome);
        Assert.True(result.IsUsable);
        second.Database.Migrate();
        Assert.Empty(second.Database.GetPendingMigrations());
    }

    // ---- what is NOT adopted, and what that must cost --------------------------------------------------

    [Theory]
    [InlineData(4)]  // older than the baseline was ported from
    [InlineData(6)]  // written by a newer build that knows something this one does not
    public void Adopt_StatisticsStoreAtAnotherVersion_IsRefusedWithANamedReasonAndLeftUntouched(int version)
    {
        BuildRealVersion5Store();

        // Move the REAL store's version stamp. The file keeps its genuine version 5 table shape, so this
        // tests the version gate itself rather than a hand-made file that differs in other ways too.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version={version}";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.IncompatibleSchemaVersion, result.Reason);
        Assert.False(result.IsUsable);
        Assert.Contains(version.ToString(), result.Detail, StringComparison.Ordinal);

        // NOT stamped. A refused store must be left exactly as it was found - the operator's file is
        // evidence, and a half-adopted one would be worse than an unadopted one.
        using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        check.Open();
        Assert.Equal(0, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'"));
    }

    [Fact]
    public void Adopt_DatabaseThatIsNotAStatisticsStore_IsRefusedWithANamedReason()
    {
        // A database carrying the right version stamp but somebody else's tables. The version alone must not
        // be enough to stamp a baseline over it.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE somebody_elses_table (id INTEGER PRIMARY KEY); " +
                                  "PRAGMA user_version=5";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.NotAStatisticsStore, result.Reason);
        Assert.False(result.IsUsable);

        // The refusal names the FOREIGN object it found, not the statistics tables it did not find. That is
        // the more useful message and it is also the safer check: this database is refused because it holds
        // something that is not ours, which is decided before the version stamp is even read - so a foreign
        // database can never reach the adoption path by carrying a version 5 stamp.
        Assert.Contains("somebody_elses_table", result.Detail, StringComparison.Ordinal);

        // And it is left exactly as it was found - this is the case that previously had sixteen statistics
        // tables and a baseline row written into it.
        using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        check.Open();
        Assert.Equal(0, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('stat_delta', '__EFMigrationsHistory')"));
    }

    /// <summary>
    /// A REFUSAL IS NOT AN EXCEPTION. Nothing about a user's file may throw out of the adoption step: the
    /// Gateway has to start and serve its roster with the statistics surface off and the reason named. This
    /// is the containment rule itself, so it is asserted directly rather than left implied by the other
    /// tests happening not to throw.
    /// </summary>
    [Fact]
    public void Adopt_NeverThrowsOnAnyStateAUsersFileCanBeIn()
    {
        BuildRealVersion5Store();

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.False(result.IsUsable);
        Assert.NotEqual(StatsStoreUnavailableReason.None, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    /// <summary>
    /// AN INTERRUPTED MIGRATION IS NOT A TRACKED STORE. Entity Framework creates the migration history table
    /// BEFORE it records what it has done, so a first migration that died partway leaves an EMPTY history
    /// beside tables that already exist.
    ///
    /// The naive check - "does a history table exist?" - reports that store as usable, and the chain then
    /// tries to build tables that are already there, throwing from Migrate() OUTSIDE this step and therefore
    /// outside its containment. "The store has a history table" and "the store is at the baseline" are two
    /// different claims, and only the second is the one the chain depends on.
    /// </summary>
    [Fact]
    public void Adopt_HistoryTableWithoutTheBaseline_IsRefusedRatherThanCertifiedAsTracked()
    {
        BuildRealVersion5Store();

        // The state an interrupted first migration leaves: the history table exists and records nothing,
        // while the sixteen tables are already there. Created with Entity Framework's OWN create script, so
        // this is the table the framework would have made rather than a hand-rolled lookalike.
        using (var context = OpenContext())
        {
            var history = context.GetService<IHistoryRepository>();
            context.Database.OpenConnection();
            context.Database.ExecuteSqlRaw(history.GetCreateScript());
        }

        SqliteConnection.ClearAllPools();

        using var check = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(check);

        Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.MigrationHistoryIncomplete, result.Reason);
        Assert.False(result.IsUsable);
        Assert.Contains("interrupted", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the ordinary new-machine paths ----------------------------------------------------------------

    [Fact]
    public void Adopt_NoFile_IsAFreshStoreAndTheChainCreatesTheSchema()
    {
        using var context = OpenContext();

        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.FreshStore, result.Outcome);
        Assert.True(result.IsUsable);

        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    /// <summary>An empty file reports PRAGMA user_version 0. It must read as a fresh store, not as an
    /// incompatible version - otherwise a zero-byte file takes the statistics surface down.</summary>
    [Fact]
    public void Adopt_EmptyFile_IsAFreshStoreRatherThanAnIncompatibleVersion()
    {
        File.WriteAllBytes(_path, Array.Empty<byte>());

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.FreshStore, result.Outcome);
        Assert.True(result.IsUsable);

        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    // ---- the caller contract ---------------------------------------------------------------------------

    /// <summary>A context that is not on SQLite is a CALLER error, not a user state, so it throws rather than
    /// being reported as an unavailable store. The two are deliberately handled differently and that split
    /// is pinned here.</summary>
    [Fact]
    public void Adopt_NonSqliteContext_ThrowsBecauseThatIsACallerErrorNotAUserState()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design")
            .Options;
        using var context = new GatewayStatsDbContext(options);

        Assert.Throws<ArgumentException>(() => GatewayStatsSqliteAdoption.Adopt(context));
    }
}
