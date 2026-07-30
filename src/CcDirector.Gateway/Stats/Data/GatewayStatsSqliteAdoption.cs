using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
    /// The schema version of the LEGACY files this adoption path exists to recognise - the shape the OLD
    /// hand-rolled code wrote, which is sitting on users' disks right now.
    ///
    /// THIS IS FROZEN FOREVER AT 5 AND MUST NEVER BE RAISED. It is a historical fact about files that already
    /// exist, not a version this build is at. Those files cannot be retroactively changed, so the number that
    /// identifies them cannot move either.
    ///
    /// IT IS DELIBERATELY A LITERAL AND NOT <see cref="GatewayStatsDatabase.SchemaVersion"/>, even though the
    /// two are both 5 today. That coincidence ENDS at the second migration in this chain. They are different
    /// concepts: this one describes a file on a disk in the past, that one describes what a build understands
    /// now. Wiring adoption to a moving value means the day anybody advances it, adoption stops recognising
    /// the very version 5 no-history files it was built to protect - and every existing self-host install
    /// silently loses its statistics, which is precisely the failure this path prevents.
    ///
    /// The version the CHAIN is currently at is a different number entirely, it advances with every
    /// migration, and it is governed by <c>GatewayStatsSqliteVersionStampTests</c>. Do not merge the two.
    /// </summary>
    public const int LegacyBaselineSchemaVersion = 5;

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

        // BEFORE ANYTHING ELSE: is a migration lock row sitting in this store?
        //
        // This runs first because every usable outcome below hands the store to the caller, whose next move
        // is Migrate() - and Migrate() takes Entity Framework's migration lock, which is acquired by retrying
        // FOREVER with no timeout and no cancellation. That is a provider constraint we do not control and
        // cannot make bounded; the only thing in our gift is to refuse before walking into it.
        //
        // The containment boundary does bound it - the caller races this whole open against a twenty-second
        // deadline, so startup survives. But without this check we would burn that entire deadline on every
        // start to learn what one query answers instantly, and because the boundary ABANDONS the wait rather
        // than cancelling it, each start would also leak a thread blocked for the life of the process.
        var lockHolder = ReadMigrationLockRow(connection);
        if (lockHolder is not null)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreLockedByAnotherProcess,
                $"The statistics store at '{path}' carries an Entity Framework migration lock taken at " +
                $"{lockHolder}. Either another process is migrating it right now, or one crashed while doing " +
                "so and left the lock behind - that row is removed on disposal, so a crash never clears it. " +
                "Running the chain would wait for it forever. The store has NOT been changed. Restart, and if " +
                "this persists the lock row must be cleared by hand. Statistics are unavailable; the rest of " +
                "the Gateway is unaffected.");

        // What the model expects, and what the file actually holds. Both read once, here, so every branch
        // below decides from the SAME picture.
        var expected = ExpectedSchema(context);
        var objects = ReadObjects(connection);

        var history = context.GetService<IHistoryRepository>();
        if (history.Exists())
            return InspectTrackedStore(context, path, expected, objects);

        // NOTHING AT ALL in the file - no table, no view, no trigger, no index of its own. Only THIS is a
        // fresh store. It is checked before the version stamp on purpose: an empty file reports user_version
        // 0, which would otherwise read as an incompatible version and take statistics down over a file with
        // nothing in it.
        //
        // "Nothing at all" rather than "no tables": a database holding a VIEW named stat_delta has no tables,
        // and calling it fresh would hand the chain a database it did not create and would then write sixteen
        // tables into.
        if (objects.Count == 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.FreshStore, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' is empty; the migration chain will create the schema.");

        // The file holds SOMETHING and has no migration history. It may only be adopted if it is genuinely a
        // version 5 store of ours - and anything else must be left strictly alone, because the caller's next
        // move is to run the chain, which WRITES.
        var foreign = objects.Keys.Where(n => !expected.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (foreign.Count > 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.NotAStatisticsStore,
                $"The database at '{path}' holds {foreign.Count} object(s) that do not belong to this store " +
                $"({string.Join(", ", foreign)}), so it is not a statistics store. It has NOT been changed in " +
                "any way and the migration chain must not be run against it. Statistics are unavailable; the " +
                "rest of the Gateway is unaffected.");

        var version = QueryUserVersion(connection);
        if (version != LegacyBaselineSchemaVersion)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.IncompatibleSchemaVersion,
                $"The statistics store at '{path}' is at schema version {version}, and only version " +
                $"{LegacyBaselineSchemaVersion} can be adopted into the migration chain. It has NOT been changed " +
                "in any way. Statistics are unavailable; the rest of the Gateway is unaffected.");

        // The rest of the claim. A stamp asserts the file is what the baseline would have BUILT, so the
        // shape is checked, not just the names.
        var mismatch = DescribeMismatch(connection, expected, objects);
        if (mismatch is not null)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreSchemaIncomplete,
                $"The database at '{path}' reports statistics schema version {version} but does not have the " +
                $"shape that version 5 builds: {mismatch}. Stamping the baseline against it would tell Entity " +
                "Framework something untrue about it, so it has NOT been changed in any way. Statistics are " +
                "unavailable; the rest of the Gateway is unaffected.");

        return StampUnderLock(context, history, connection, path, expected.Count, version);
    }

    /// <summary>
    /// Take the migration lock, confirm the store STILL needs adopting, and stamp it.
    ///
    /// TWO GATEWAYS CAN OPEN THE SAME FILE. Inspection and stamping are separated by the whole of the
    /// eligibility check, so without a lock two adopters both see "no history", both try to CREATE the
    /// history table, and the loser's create fails. That failure would land in the boundary catch and be
    /// reported as <see cref="StatsStoreUnavailableReason.StoreUnreadable"/> - a healthy, correctly adopted
    /// database described as unreadable, leaving that Gateway with statistics off until someone restarts it.
    ///
    /// THE LOCK IS TAKEN HERE AND NOT AROUND THE INSPECTION, deliberately. Acquiring it WRITES a lock table
    /// into whatever file it is pointed at, and the inspection's whole job is to decide whether we are
    /// allowed to write to this file at all. Taking it first would put a table into the foreign databases
    /// finding 2 exists to refuse. By this line eligibility is established and stamping is legitimate.
    ///
    /// Re-checking under the lock is not a fallback - it is reading the state again once it can no longer
    /// change, and reporting what is actually true.
    /// </summary>
    /// <summary>How long to wait for another writer before giving up and reporting the store busy. A BOUND,
    /// not a retry budget: whatever happens, this path returns.</summary>
    private const int WriteLockWaitMilliseconds = 5_000;

    private static StatsStoreAdoptionResult StampUnderLock(
        GatewayStatsDbContext context, IHistoryRepository history, SqliteConnection connection,
        string path, int tableCount, int version)
    {
        var baseline = BaselineMigrationOf(context);

        // SQLite's OWN write lock, bounded by busy_timeout - deliberately NOT Entity Framework's migration
        // lock. That one creates a __EFMigrationsLock row and retries forever with no timeout and no
        // cancellation, and its row is removed on DISPOSAL, so a process that crashes holding it leaves it
        // behind permanently. A later adoption then waits for ever, INSIDE a path whose entire contract is
        // containment - it does not even throw, so the boundary catch never sees it and the caller simply
        // hangs. That is this mission's own failure mode (a statistics fault stalling startup) reintroduced
        // by the fix for the check-then-create race, which is worse than the race it was fixing.
        //
        // BEGIN IMMEDIATE takes the write lock UP FRONT, so the re-check below cannot be raced by a second
        // adopter between reading and writing - which is the actual thing that needed serialising. There is
        // no lock row to leak: SQLite releases the lock when the connection goes, crash included.
        Execute(connection, $"PRAGMA busy_timeout = {WriteLockWaitMilliseconds}");

        SqliteTransaction transaction;
        try
        {
            transaction = connection.BeginTransaction(deferred: false);
        }
        catch (SqliteException ex)
        {
            // Another process holds the write lock and did not release it inside the bound. Nothing has been
            // changed, and the next startup will simply try again.
            FileLog.Write($"[GatewayStatsSqliteAdoption] StampUnderLock: path={path} busy: {ex.Message}");
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreLockedByAnotherProcess,
                $"The statistics store at '{path}' is being written by another process and did not become " +
                $"available within {WriteLockWaitMilliseconds} milliseconds, so it has NOT been changed. " +
                "Restart, and if this persists a process is stuck holding the file. The rest of the Gateway " +
                "is unaffected.");
        }

        using (transaction)
        {
            // Re-read under the write lock, where the answer can no longer change.
            if (HistoryTableExists(connection, transaction))
            {
                if (HistoryRecords(connection, transaction, baseline))
                    return new StatsStoreAdoptionResult(
                        StatsStoreAdoptionOutcome.AlreadyTracked, StatsStoreUnavailableReason.None,
                        $"The statistics store at '{path}' was adopted by another instance while this one " +
                        "was inspecting it; it records the baseline migration as applied. Nothing to do.");

                return new StatsStoreAdoptionResult(
                    StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreSchemaIncomplete,
                    $"The statistics store at '{path}' gained a migration history table while this instance " +
                    "was inspecting it, and that history does not record the baseline migration. Another " +
                    "instance may be part-way through adopting or migrating it. The store has NOT been " +
                    "changed here. Statistics are unavailable; the rest of the Gateway is unaffected.");
            }

            Stamp(connection, transaction, history, path, baseline);
            transaction.Commit();
        }

        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.Adopted, StatsStoreUnavailableReason.None,
            $"Adopted the statistics store at '{path}': it was at schema version {version} with all " +
            $"{tableCount} tables present in the right shape and no migration history, so the history " +
            "table was created and the baseline migration stamped as applied. No row was read, written or moved.");
    }

    /// <summary>
    /// The timestamp on Entity Framework's migration lock row, or null if no lock is held.
    ///
    /// The row is what <c>Migrate()</c> waits on. Entity Framework stores a timestamp beside it but does NOT
    /// use it for expiry - nothing ever times a lock out - so it is read here only to tell the operator HOW
    /// OLD the lock is, which is what distinguishes "a migration is running right now" from "a migration
    /// died three weeks ago".
    /// </summary>
    private static string? ReadMigrationLockRow(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Timestamp\" FROM \"__EFMigrationsLock\" LIMIT 1";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : value.ToString();
    }

    private static bool HistoryTableExists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        command.Parameters.AddWithValue("$n", "__EFMigrationsHistory");
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool HistoryRecords(SqliteConnection connection, SqliteTransaction transaction, string migration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $m";
        command.Parameters.AddWithValue("$m", migration);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
        GatewayStatsDbContext context, string path,
        IReadOnlyDictionary<string, ExpectedTable> expected, IReadOnlyDictionary<string, string> objects)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var baseline = BaselineMigrationOf(context);
        var applied = context.Database.GetAppliedMigrations().ToList();

        if (applied.Contains(baseline, StringComparer.Ordinal))
        {
            // The steady state, and by far the common one - but "the history says the baseline ran" is a
            // CLAIM ABOUT THE PAST, and the tables are the present. A store whose stat_delta has since been
            // dropped records the baseline, reports nothing pending, and dies on the first query. Reporting
            // it usable puts that failure outside this step's containment, so the shape is checked here too.
            var damaged = DescribeMismatch(connection, expected, objects);
            if (damaged is not null)
                return new StatsStoreAdoptionResult(
                    StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreSchemaIncomplete,
                    $"The statistics store at '{path}' records the baseline migration as applied, but the " +
                    $"database no longer matches it: {damaged}. Nothing is pending, so the chain would report " +
                    "success and the first query would fail. The store has NOT been changed and needs looking " +
                    "at by hand. Statistics are unavailable; the rest of the Gateway is unaffected.");

            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.AlreadyTracked, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' records the baseline migration as applied and has all " +
                $"{expected.Count} tables in the right shape; nothing to adopt.");
        }

        // The baseline is NOT recorded. The caller's next move is to run the chain, which WRITES - so this
        // may only be called fresh when the database is genuinely empty AND has no migration history of its
        // own. A foreign database carrying its own Entity Framework history satisfies neither, and certifying
        // it fresh is how sixteen statistics tables get written into somebody else's database.
        if (applied.Count == 0 && objects.Count == 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.FreshStore, StatsStoreUnavailableReason.None,
                $"The statistics store at '{path}' has a migration history table that records nothing and " +
                "holds no objects of its own; the migration chain will create the schema.");

        var foreign = objects.Keys.Where(n => !expected.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (foreign.Count > 0 || applied.Count > 0)
            return new StatsStoreAdoptionResult(
                StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.NotAStatisticsStore,
                $"The database at '{path}' has a migration history recording {applied.Count} migration(s), " +
                $"none of them this store's baseline '{baseline}', and holds {foreign.Count} object(s) that " +
                "are not this store's. It belongs to something else. It has NOT been changed in any way and " +
                "the migration chain must not be run against it. Statistics are unavailable; the rest of the " +
                "Gateway is unaffected.");

        // Our own tables, present, with a history that records nothing: a migration was interrupted partway.
        // The same STATE as a store missing a table or a column - half-built, no safe automatic repair - so
        // it carries the same reason. Which route found it is carried in the detail, not in the reason.
        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.NotAdoptable, StatsStoreUnavailableReason.StoreSchemaIncomplete,
            $"The statistics store at '{path}' has a migration history table that does NOT record the " +
            $"baseline migration '{baseline}', but {objects.Count} of its {expected.Count} tables already " +
            "exist. A migration was interrupted partway. Running the chain would try to create tables that " +
            "are already there, so the store has NOT been changed and needs looking at by hand - restore it " +
            "from a backup, or move it aside to start a fresh one. Statistics are unavailable; the rest of " +
            "the Gateway is unaffected.");
    }

    /// <summary>
    /// The shape this store expects, read off the MODEL rather than written out as a list.
    ///
    /// A CAVEAT THAT MUST NOT BE FORGOTTEN, because the comment here used to overclaim: the SQLite baseline
    /// is hand-written data definition language, so reading the expectation from the model does NOT
    /// automatically make it agree with what the baseline builds. The two agreed only once the model was
    /// corrected, and the thing that actually holds them together is
    /// <c>GatewayStatsSqliteBaselineEquivalenceTests</c>, which compares a baseline-built database against one
    /// built by running the old code. This is the model's opinion, and it is checked elsewhere.
    /// </summary>
    private sealed record ExpectedTable(
        HashSet<string> Columns,
        List<string> PrimaryKeyColumns,
        Dictionary<string, bool> ColumnIsNullable,
        Dictionary<string, string?> ColumnDefaults,
        List<string> IndexNames);

    private static Dictionary<string, ExpectedTable> ExpectedSchema(GatewayStatsDbContext context)
    {
        var schema = new Dictionary<string, ExpectedTable>(StringComparer.Ordinal);
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;

            var properties = entity.GetProperties().ToList();
            var key = entity.FindPrimaryKey();

            schema[table] = new ExpectedTable(
                Columns: new HashSet<string>(properties.Select(p => p.GetColumnName()), StringComparer.Ordinal),
                PrimaryKeyColumns: key is null
                    ? new List<string>()
                    : key.Properties.Select(p => p.GetColumnName()).ToList(),
                ColumnIsNullable: properties.ToDictionary(
                    p => p.GetColumnName(), p => p.IsNullable, StringComparer.Ordinal),
                // The ANNOTATION, not GetDefaultValue(). GetDefaultValue() hands back the CLR default for a
                // non-nullable value type - 0 for a long - whether or not a database default was ever
                // configured, so comparing against it condemns every healthy store for not having a DEFAULT 0
                // on every integer column. The control case caught that immediately, which is the whole
                // reason a healthy-store assertion sits beside every tightening.
                ColumnDefaults: properties.ToDictionary(
                    p => p.GetColumnName(),
                    p => p.FindAnnotation(RelationalAnnotationNames.DefaultValue)?.Value?.ToString(),
                    StringComparer.Ordinal),
                IndexNames: entity.GetIndexes()
                    .Select(i => i.GetDatabaseName())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList());
        }
        return schema;
    }

    /// <summary>
    /// Every object the database holds that is not SQLite's own and not Entity Framework's bookkeeping, as
    /// name to type. VIEWS and triggers are included, not just tables: a database holding a view named
    /// stat_delta holds no tables at all, and treating it as empty would hand the chain a database it did not
    /// create and then write sixteen tables into it.
    /// </summary>
    private static Dictionary<string, string> ReadObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";

        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (IsEntityFrameworkBookkeeping(name)) continue;
            // Indexes belong to their tables and are not independent evidence about what the file holds.
            var type = reader.GetString(1);
            if (string.Equals(type, "index", StringComparison.Ordinal)) continue;
            objects[name] = type;
        }
        return objects;
    }

    /// <summary>Entity Framework's own two bookkeeping tables, which are never part of the statistics
    /// schema and are what adoption ADDS rather than what it inspects.</summary>
    private static bool IsEntityFrameworkBookkeeping(string name) =>
        string.Equals(name, "__EFMigrationsHistory", StringComparison.Ordinal) ||
        string.Equals(name, "__EFMigrationsLock", StringComparison.Ordinal);

    /// <summary>
    /// Describe the first way the database fails to be the shape the baseline builds, or null if it matches.
    ///
    /// COLUMNS, NOT JUST TABLE NAMES. A stamp asserts the file is what the baseline would have BUILT, and a
    /// table name proves only that something with that name exists. A real version 5 file whose stat_delta
    /// had been recreated as a single id column passed a names-only check, was stamped, and then failed on
    /// "no such column: s.chars" - a store certified as adopted and immediately broken.
    /// </summary>
    private static string? DescribeMismatch(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ExpectedTable> expected,
        IReadOnlyDictionary<string, string> objects)
    {
        var missing = expected.Keys.Where(t => !objects.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            return $"{missing.Count} of the {expected.Count} tables it should have are absent " +
                   $"({string.Join(", ", missing)})";

        var notTables = expected.Keys
            .Where(t => !string.Equals(objects[t], "table", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (notTables.Count > 0)
            return $"{notTables.Count} of its names are not tables " +
                   $"({string.Join(", ", notTables.Select(t => $"{t} is a {objects[t]}"))})";

        // MISSING columns refuse. EXTRA columns are TOLERATED. The two failures are not symmetric and it
        // would be a mistake to treat them as one check:
        //
        //  - A MISSING column breaks queries loudly and immediately, so refusing is the only safe answer.
        //  - An EXTRA column is harmless to every query this store runs, because all sixteen tables are read
        //    by an explicit column list - swept in both directions and true as a MEASURED fact, not an
        //    assumption. So refusing on it buys nothing concrete, and costs the worse failure mode: it
        //    CONDEMNS A HEALTHY STORE. In this design that is silent and permanent - the Gateway serves fine,
        //    statistics are off, the named reason is a lie, and nothing pages anyone.
        //
        // There is also a specific reason strictness here would be redundant. The realistic way a store gains
        // a column is a NEWER build adding one and the user then rolling back - and that store's version
        // stamp is HIGHER, so the version check above refuses it first, more precisely, and with a message
        // about versions rather than columns.
        foreach (var table in expected.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var want = expected[table];
            var actual = ReadColumns(connection, table);

            var missingColumns = want.Columns.Except(actual.Keys, StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (missingColumns.Count > 0)
                return $"table {table} is missing {missingColumns.Count} column(s) " +
                       $"({string.Join(", ", missingColumns)})";

            // A COLUMN NAME PROVES ALMOST NOTHING ON ITS OWN, which a review demonstrated by recreating
            // stat_delta with the exact expected names and no primary key, no NOT NULL, no default and no
            // indexes - and watching it be adopted and stamped. Adoption asserts the file is what the
            // baseline would have BUILT, so the structure is checked, not just the vocabulary.

            var actualKey = actual.Values.Where(c => c.KeyOrdinal > 0)
                .OrderBy(c => c.KeyOrdinal).Select(c => c.Name).ToList();
            if (!actualKey.SequenceEqual(want.PrimaryKeyColumns, StringComparer.Ordinal))
                return $"table {table} has primary key ({FormatKey(actualKey)}) where the baseline builds " +
                       $"({FormatKey(want.PrimaryKeyColumns)})";

            // A single INTEGER primary key is a rowid ALIAS, and SQLite reports notnull 0 for it however it
            // was declared - version 5 writes a bare INTEGER PRIMARY KEY while the model calls it required.
            // That difference is real and unavoidable, so the nullability of a rowid key is not checked; it
            // is the one column whose notnull carries no information.
            var rowidKey = want.PrimaryKeyColumns.Count == 1 &&
                           actual.TryGetValue(want.PrimaryKeyColumns[0], out var pk) &&
                           pk.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                ? want.PrimaryKeyColumns[0]
                : null;

            foreach (var column in want.Columns.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (string.Equals(column, rowidKey, StringComparison.Ordinal)) continue;

                var isNullable = !actual[column].NotNull;
                if (isNullable != want.ColumnIsNullable[column])
                    return $"table {table} column {column} is " +
                           (isNullable ? "nullable" : "NOT NULL") + " where the baseline builds it " +
                           (want.ColumnIsNullable[column] ? "nullable" : "NOT NULL");

                var wantDefault = want.ColumnDefaults[column];
                if (wantDefault is not null && !DefaultMatches(actual[column].Default, wantDefault))
                    return $"table {table} column {column} has default " +
                           $"{actual[column].Default ?? "<none>"} where the baseline builds it '{wantDefault}'";
            }

            var indexes = ReadIndexNames(connection, table);
            var missingIndexes = want.IndexNames.Where(i => !indexes.Contains(i))
                .OrderBy(i => i, StringComparer.Ordinal).ToList();
            if (missingIndexes.Count > 0)
                return $"table {table} is missing {missingIndexes.Count} index(es) " +
                       $"({string.Join(", ", missingIndexes)})";
        }

        return null;
    }

    private static string FormatKey(IReadOnlyList<string> columns) =>
        columns.Count == 0 ? "no primary key" : string.Join(", ", columns);

    /// <summary>SQLite reports a text default with its quotes ('local'); the model holds the bare value.</summary>
    private static bool DefaultMatches(string? actual, string expected) =>
        actual is not null &&
        (string.Equals(actual, expected, StringComparison.Ordinal) ||
         string.Equals(actual.Trim('\''), expected, StringComparison.Ordinal));

    private sealed record ColumnFacts(string Name, string Type, bool NotNull, string? Default, int KeyOrdinal);

    private static Dictionary<string, ColumnFacts> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);

        var columns = new Dictionary<string, ColumnFacts>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            columns[name] = new ColumnFacts(
                name,
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetValue(3).ToString(),
                (int)reader.GetInt64(4));
        }
        return columns;
    }

    private static HashSet<string> ReadIndexNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_list($table)";
        command.Parameters.AddWithValue("$table", table);

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

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
    private static void Stamp(
        SqliteConnection connection, SqliteTransaction transaction, IHistoryRepository history,
        string path, string baseline)
    {
        FileLog.Write($"[GatewayStatsSqliteAdoption] Stamp: path={path}, baseline={baseline}");

        // The two statements come from Entity Framework's OWN history repository rather than being written
        // out here, so the history table this creates is byte-identical to the one the chain would have
        // created itself and cannot drift from it when the framework version moves. They are executed on the
        // caller's write transaction, which already holds the file's write lock.
        foreach (var sql in new[]
                 {
                     history.GetCreateScript(),
                     history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())),
                 })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

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
