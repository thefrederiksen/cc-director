using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// ADOPTION of an existing self-host SQLite statistics store into the Entity Framework migration chain.
///
/// THE PROBLEM THIS SOLVES, which is not optional and is not hypothetical. Every self-host user who has ever
/// opened the statistics page has a gateway-stats.db on disk written by the hand-rolled
/// <see cref="GatewayStatsDatabase"/>: sixteen tables, PRAGMA user_version 5, and NO
/// <c>__EFMigrationsHistory</c> table, because that path never used Entity Framework. Point a migration chain
/// at that file and the baseline migration tries to CREATE sixteen tables that already exist, and the open
/// fails with "table stat_delta already exists".
///
/// THE ANSWER IS ADOPTION - not retirement, and not a fallback. The rows in that file are ALREADY the right
/// shape: the model was ported from that exact schema, table name for table name and column name for column
/// name, precisely so that this would be true. The only thing missing is the bookkeeping that says so. So the
/// step creates the history table and stamps the baseline migration as applied, and the chain then proceeds
/// normally from there - a store that was written before Entity Framework existed here becomes a store the
/// chain understands, with no data copied, no table rebuilt and nothing lost.
///
/// It is deliberately EXPLICIT, NAMED and LOGGED rather than a quiet repair. A store silently reshaping
/// itself on startup is how numbers disappear without anybody noticing which build did it.
///
/// WHAT IS *NOT* ADOPTED, and this is the guard that keeps the step honest. Adoption is a claim that the file
/// already matches the baseline. That claim is only true for a version 5 statistics store, so the step checks
/// BOTH halves - the version stamp AND that every table the model expects is actually present - and adopts
/// nothing else. A file at any other user_version, or a file that is not this store at all, is reported
/// UNAVAILABLE WITH A NAMED REASON and never stamped.
///
/// FAIL LOUD IS NOT FAIL FATAL. Nothing about a user's FILE throws out of here; it comes back as a
/// <see cref="StatsStoreAdoptionResult"/> so the Gateway still starts and still serves its roster with the
/// statistics surface off and the reason named. The mission exists because a statistics fault took the
/// primary read path down for 32 minutes on the hosted Gateway, and a version check that bricks a working
/// desktop Gateway would be that same incident on the other surface. A CALLER contract violation - handing
/// this a context that is not on SQLite - is a programming error rather than a user state, and that does
/// throw.
///
/// The main <see cref="Data.GatewayDatabase"/> keeps its current fatal-on-failure startup behaviour, which is
/// correct for the database that carries the roster, and is not touched by any of this.
/// </summary>
public static class GatewayStatsSqliteAdoption
{
    /// <summary>
    /// The SQLite schema version an adoptable store reports in <c>PRAGMA user_version</c> - the version the
    /// Entity Framework baseline migration was ported FROM, and therefore the only version whose on-disk
    /// shape the baseline can honestly be stamped as having produced.
    ///
    /// Read from <see cref="GatewayStatsDatabase.SchemaVersion"/> rather than written as a literal 5, so it
    /// cannot quietly disagree with the code that actually wrote those files.
    /// </summary>
    public static int AdoptableSchemaVersion => GatewayStatsDatabase.SchemaVersion;

    /// <summary>
    /// Adopt the SQLite statistics store behind <paramref name="context"/> if it needs adopting, and report
    /// whether the migration chain may now run against it.
    ///
    /// Call this BEFORE <c>Database.Migrate()</c>, and only when the result is usable.
    /// </summary>
    /// <param name="context">A statistics context built on the SQLite provider.</param>
    /// <returns>What was found and done, and - when the store cannot be used - the named reason.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The context is not on the SQLite provider. That is a caller
    /// contract violation (a programming error), not a state a user's machine can be in, so it throws rather
    /// than being reported as an unavailable store.</exception>
    public static StatsStoreAdoptionResult Adopt(GatewayStatsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.GetDbConnection() is not SqliteConnection connection)
            throw new ArgumentException(
                "Statistics store adoption applies to the SQLite provider only; this context is on " +
                $"'{context.Database.ProviderName}'. A Postgres statistics store is created by its own " +
                "migration chain and has never existed without a migration history table, so there is " +
                "nothing to adopt.", nameof(context));

        var path = connection.DataSource;
        FileLog.Write($"[GatewayStatsSqliteAdoption] Adopt: path={path}");

        try
        {
            var result = Inspect(context, connection, path);
            FileLog.Write($"[GatewayStatsSqliteAdoption] Adopt: path={path}, outcome={result.Outcome}, " +
                          $"reason={result.Reason}: {result.Detail}");
            return result;
        }
        catch (Exception ex)
        {
            // A boundary catch, and the only one here. The file could not be read or interrogated at all - a
            // locked, truncated or corrupt file, or a path that is not a readable database. That is a state a
            // real machine can be in, so it is REPORTED with a named reason rather than thrown: the Gateway
            // still starts and still serves its roster, with the statistics surface off.
            FileLog.Write($"[GatewayStatsSqliteAdoption] Adopt FAILED: path={path}: {ex.Message}");
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable,
                StatsStoreUnavailableReason.StoreUnreadable,
                $"The statistics store at '{path}' could not be read: {ex.Message}. Statistics are " +
                "unavailable; the rest of the Gateway is unaffected.");
        }
    }

    private static StatsStoreAdoptionResult Inspect(
        GatewayStatsDbContext context, SqliteConnection connection, string path)
    {
        // No file at all: the ordinary new-machine path. The chain creates the schema from scratch.
        if (!File.Exists(path))
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.FreshStore, StatsStoreUnavailableReason.None,
                $"No statistics store exists at '{path}' yet; the migration chain will create one.");

        context.Database.OpenConnection();

        // The tables this store expects, read off the MODEL rather than written out as a list here, so the
        // expectation cannot drift from the schema the baseline migration actually creates.
        var expected = ExpectedTableNames(context);
        var present = ReadTableNames(connection);
        var missing = expected.Where(t => !present.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();

        var history = context.GetService<IHistoryRepository>();
        if (history.Exists())
            return InspectTrackedStore(context, path, expected.Count, missing.Count);

        // A file with no tables is an empty or newly-created file, not a store to adopt. It is checked BEFORE
        // the version stamp on purpose: an empty file reports user_version 0, which would otherwise be read as
        // an incompatible version and take the statistics surface down over a file with nothing in it.
        if (CountUserTables(connection) == 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.FreshStore, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' is empty; the migration chain will create the schema.");

        var version = QueryUserVersion(connection);
        if (version != AdoptableSchemaVersion)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.IncompatibleSchemaVersion,
                $"The statistics store at '{path}' is at schema version {version}, and only version " +
                $"{AdoptableSchemaVersion} can be adopted into the migration chain. It has NOT been changed " +
                "in any way. Statistics are unavailable; the rest of the Gateway is unaffected.");

        // The second half of the claim. The version stamp alone is not enough: stamping a baseline is an
        // assertion that these exact tables are already there, so it is checked rather than assumed.
        if (missing.Count > 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.NotAStatisticsStore,
                $"The database at '{path}' reports statistics schema version {version} but is missing " +
                $"{missing.Count} of the {expected.Count} tables this store expects ({string.Join(", ", missing)}), " +
                "so it is not a statistics store and has NOT been changed in any way. Statistics are " +
                "unavailable; the rest of the Gateway is unaffected.");

        Stamp(context, history, path);

        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.Adopted, StatsStoreUnavailableReason.None,
            $"Adopted the statistics store at '{path}': it was at schema version {version} with all " +
            $"{expected.Count} tables present and no migration history, so the history table was created and " +
            $"the baseline migration stamped as applied. No row was read, written or moved.");
    }

    /// <summary>
    /// Decide about a store that ALREADY has a migration history table.
    ///
    /// "The store has a history table" and "the store is at the baseline" are TWO DIFFERENT CLAIMS, and only
    /// the second is the one the chain depends on. Treating the first as though it were the second is how an
    /// interrupted migration turns into a crash: Entity Framework creates the history table before it records
    /// what it has done, so a first migration that died partway leaves an EMPTY history sitting beside tables
    /// that already exist. Reporting that store as usable hands the chain a database it will try to build
    /// again, and the resulting "table already exists" would be thrown from <c>Migrate()</c> - OUTSIDE this
    /// step, and therefore outside its containment.
    ///
    /// Adoption itself can never produce that state (it stamps the history table and the baseline row in one
    /// transaction), so this is not defending against our own step. It is refusing to certify a state we did
    /// not create and cannot honestly repair: which half of an interrupted migration actually landed is a
    /// guess, and guessing it is how a store loses data quietly.
    /// </summary>
    private static StatsStoreAdoptionResult InspectTrackedStore(
        GatewayStatsDbContext context, string path, int expectedTableCount, int missingTableCount)
    {
        var baseline = BaselineMigrationOf(context);
        var applied = context.Database.GetAppliedMigrations().ToList();

        // The steady state, and by far the common one: every store adopted once, and every store the chain
        // itself created, arrives here on every later startup.
        if (applied.Contains(baseline, StringComparer.Ordinal))
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.AlreadyTracked, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' already records the baseline migration as applied; " +
                "nothing to adopt.");

        // A history table with no baseline recorded, and no tables either: nothing was ever built here, so
        // the chain simply builds it. This is a fresh store that happens to have been touched.
        if (missingTableCount == expectedTableCount)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.FreshStore, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' has a migration history table but records no migrations " +
                "and holds none of this store's tables; the migration chain will create the schema.");

        // A history table that does not record the baseline, beside tables that ARE there.
        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.MigrationHistoryIncomplete,
            $"The statistics store at '{path}' has a migration history table that does NOT record the " +
            $"baseline migration '{baseline}', but {expectedTableCount - missingTableCount} of its " +
            $"{expectedTableCount} tables already exist. A migration was interrupted partway. Running the " +
            "chain would try to create tables that are already there, so the store has NOT been changed and " +
            "needs looking at by hand. Statistics are unavailable; the rest of the Gateway is unaffected.");
    }

    /// <summary>The tables this store expects, read off the MODEL rather than written out as a list, so the
    /// expectation cannot drift from the schema the baseline migration creates.</summary>
    private static List<string> ExpectedTableNames(GatewayStatsDbContext context) =>
        context.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Create the migration history table and stamp the baseline migration as applied.
    ///
    /// The two statements come from Entity Framework's OWN history repository rather than being written out
    /// here, so the history table this creates is byte-identical to the one the chain would have created
    /// itself and cannot drift from it when the framework version moves.
    ///
    /// Both run in ONE transaction. A crash between them would leave a history table with no baseline row,
    /// which is WORSE than the state we started in: the chain would read an empty history, decide the
    /// baseline is pending, and try again to create sixteen tables that exist.
    /// </summary>
    private static void Stamp(GatewayStatsDbContext context, IHistoryRepository history, string path)
    {
        var baseline = BaselineMigrationOf(context);

        FileLog.Write($"[GatewayStatsSqliteAdoption] Stamp: path={path}, baseline={baseline}");

        using var transaction = context.Database.BeginTransaction();
        context.Database.ExecuteSqlRaw(history.GetCreateScript());
        context.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())));
        transaction.Commit();

        FileLog.Write($"[GatewayStatsSqliteAdoption] Stamp: path={path}, baseline={baseline} applied");
    }

    /// <summary>
    /// The first migration in this context's chain - the one whose Up() creates the sixteen tables, and
    /// therefore the only one a version 5 store can honestly be stamped as having already applied.
    ///
    /// Read from the chain rather than held as a constant, because a constant naming the baseline is a
    /// measurement that goes stale the moment the chain is regenerated, and a baseline that has quietly
    /// stopped being first would stamp the WRONG migration as applied - which reads as success and leaves the
    /// store one migration out of step.
    /// </summary>
    /// <exception cref="InvalidOperationException">The chain is empty. That is a build defect - the
    /// migrations assembly was not shipped or was not built - not a state of the user's file.</exception>
    private static string BaselineMigrationOf(GatewayStatsDbContext context)
    {
        var baseline = context.Database.GetMigrations().FirstOrDefault();
        if (string.IsNullOrEmpty(baseline))
            throw new InvalidOperationException(
                "The statistics migration chain is empty, so there is no baseline migration to stamp. The " +
                "migrations assembly is missing from the build.");
        return baseline;
    }

    private static int QueryUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var result = command.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private static int CountUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static HashSet<string> ReadTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }
}
