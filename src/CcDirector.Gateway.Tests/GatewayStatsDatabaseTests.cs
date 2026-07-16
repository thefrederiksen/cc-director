using CcDirector.Gateway.Stats;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway statistics database foundation (mission "SQLite on the Gateway", Phase 1): the file opens,
/// the schema is created, and the schema VERSION is real from day one.
///
/// The version is the point of these tests, not ceremony. The stores this database replaces had no schema
/// version at all, so a shape change either quarantined the file and lost the numbers (pull request #1376
/// wiped the all-time concurrency peak exactly this way) or - worse - deserialized to defaults and came up
/// silently with zeros. PRAGMA user_version is what makes the next shape change a migration instead of a
/// loss, so it is pinned here.
/// </summary>
public sealed class GatewayStatsDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static int UserVersion(GatewayStatsDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool TableExists(GatewayStatsDatabase db, string table)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    [Fact]
    public void Open_NewFile_CreatesFileAndStampsSchemaVersion()
    {
        using var db = new GatewayStatsDatabase(_path);

        Assert.True(File.Exists(_path));
        Assert.Equal(GatewayStatsDatabase.SchemaVersion, UserVersion(db));
    }

    [Fact]
    public void Open_NewFile_CreatesEverySchemaTable()
    {
        using var db = new GatewayStatsDatabase(_path);

        // The row table and the live operational state.
        Assert.True(TableExists(db, "stat_delta"));
        Assert.True(TableExists(db, "session_highwater"));
        // The all-time distinct sets, deliberately never pruned.
        Assert.True(TableExists(db, "wingman_session"));
        Assert.True(TableExists(db, "repo_session"));
        Assert.True(TableExists(db, "agent_session"));
        // Folded grouping key to first-seen display spelling.
        Assert.True(TableExists(db, "repo_identity"));
        Assert.True(TableExists(db, "agent_identity"));
        Assert.True(TableExists(db, "model_identity"));
        // The checkout dimension (schema v4): the local working directory retained beside the repository slug.
        Assert.True(TableExists(db, "checkout_identity"));
        // Token spend (issue #1637) - the delta lane and its per-session high-water.
        Assert.True(TableExists(db, "token_delta"));
        Assert.True(TableExists(db, "token_highwater"));
        // The agent-to-agent lane (issue #1636) - its OWN tables, so these turns cannot be summed
        // into the human voice-versus-typed totals by accident.
        Assert.True(TableExists(db, "agent_driven_delta"));
        Assert.True(TableExists(db, "agent_driven_highwater"));
        // Sessions already back-filled to their agent (issue #1633).
        Assert.True(TableExists(db, "agents_seeded"));
        // Runtime scalars - agents_since_utc, stamped on first observation. NOT a baseline.
        Assert.True(TableExists(db, "meta"));

        // NO baseline tables. The owner chose not to carry the old numbers across, so the past is not
        // a baseline any more - it is simply gone, and gateway-input-stats.json is renamed aside
        // unread. These assertions exist so that a reappearing baseline table is a test failure rather
        // than a quietly rebuilt import nobody asked for.
        Assert.False(TableExists(db, "baseline_total"));
        Assert.False(TableExists(db, "baseline_hour"));
        Assert.False(TableExists(db, "baseline_repo"));
        Assert.False(TableExists(db, "baseline_agent"));
        Assert.False(TableExists(db, "baseline_agent_driven"));
        Assert.False(TableExists(db, "baseline_scalar"));
    }

    [Fact]
    public void Open_ExistingFile_ReopensAtSameVersionAndKeepsData()
    {
        using (var first = new GatewayStatsDatabase(_path))
        {
            using var cmd = first.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(name, value) VALUES ('probe', '42')";
            cmd.ExecuteNonQuery();
        }

        using var second = new GatewayStatsDatabase(_path);

        Assert.Equal(GatewayStatsDatabase.SchemaVersion, UserVersion(second));
        using var read = second.Connection.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE name='probe'";
        // Re-opening must not re-run the migration over live data. CREATE TABLE IF NOT EXISTS is only half
        // of that promise; this is the half that would actually notice.
        Assert.Equal("42", read.ExecuteScalar() as string);
    }

    [Fact]
    public void Open_FileFromNewerBuild_FailsLoudlyRatherThanDowngrading()
    {
        // A database written by a build that knows a shape this one does not. Opening it anyway would be a
        // silent downgrade - exactly the class of failure this mission exists to end - so it must throw, and
        // the message must tell the owner what to do about it.
        using (var db = new GatewayStatsDatabase(_path))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version={GatewayStatsDatabase.SchemaVersion + 1}";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => new GatewayStatsDatabase(_path));

        Assert.Contains("newer build", ex.Message);
        Assert.Contains((GatewayStatsDatabase.SchemaVersion + 1).ToString(), ex.Message);
    }

    [Fact]
    public void Open_EnablesWriteAheadLogging()
    {
        using var db = new GatewayStatsDatabase(_path);

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (cmd.ExecuteScalar() as string)?.ToLowerInvariant());
    }

    [Fact]
    public void StatDelta_CarriesSurrogateIds_AndNoRepositoryOrAgentStringInAnyForm()
    {
        // The point of the surrogate id (brief Decision 2, revision 7): SQLite must never be in a position
        // to compare a repository or agent string, because its BINARY collation would answer a
        // case-sensitive question where the code today asks a case-insensitive one
        // (GatewayInputStatsAggregator.cs:55 and :61). A folded string column cannot fix that - it would
        // need a normalizer exactly equivalent to StringComparer.OrdinalIgnoreCase, and no such function
        // exists. So the schema must not be ABLE to hold the string at all.
        //
        // This test pins the shape rather than a behaviour, deliberately: it is the schema, not a rule
        // anybody has to remember, that makes the mistake impossible. A future hand adding a repo TEXT
        // column back fails here.
        // An EXHAUSTIVE whitelist, not a list of forbidden names. The first version of this test asserted
        // DoesNotContain("repo", ...), which xUnit reads as "no column named exactly repo" - so it would
        // have passed happily against repo_raw, repo_text, repository, or any other repository string column
        // added alongside repo_id. It asserted strictly less than its own name claimed, which is the exact
        // defect this mission keeps finding: a test that looks like coverage.
        //
        // A whitelist cannot rot that way. Any new column - whatever it is called - fails here until someone
        // states it deliberately, which is the point when they must justify it against Decision 2.
        using var db = new GatewayStatsDatabase(_path);

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type FROM pragma_table_info('stat_delta')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns[reader.GetString(0)] = reader.GetString(1);
        }

        var expected = new[]
        {
            "id", "hour_utc", "session_id", "modality", "surface",
            "is_voice", "repo_id", "checkout_id", "model_id", "wingman", "turns", "chars",
        };
        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal),
                     columns.Keys.OrderBy(c => c, StringComparer.Ordinal));

        // The repository and model dimensions are integers, so SQLite cannot be asked to compare a
        // repository or model string here even by accident. model_id is stated here deliberately, which is
        // what this whitelist exists to force: it is a surrogate id for the SAME reason repo_id is - the
        // model is free text with unbounded cardinality and casing by convention only, so the in-memory
        // OrdinalIgnoreCase map must be the only thing that ever decides two model names are one model.
        Assert.Equal("INTEGER", columns["repo_id"]);
        Assert.Equal("INTEGER", columns["model_id"]);
        // checkout_id is a surrogate id too, for the same reason: the local path is free text and only the
        // in-memory OrdinalIgnoreCase map may decide two paths are one checkout.
        Assert.Equal("INTEGER", columns["checkout_id"]);

        // Ruling B: agent_id is NOT on stat_delta. The agent tally is not derivable from these rows -
        // the first-fold back-fill attributes turns that are already in the totals, so a row here would
        // inflate them and no row would lose the attribution. It has its own table.
        Assert.DoesNotContain("agent_id", columns.Keys);
        Assert.True(TableExists(db, "agent_delta"));
    }

    [Fact]
    public void RepoIdentity_AssignsDistinctSurrogateIds_AndKeepsTheDisplaySpellingVerbatim()
    {
        // The identity table stores the FIRST-SEEN spelling, which is what a .NET Dictionary with an
        // OrdinalIgnoreCase comparer does - it keeps the key it was first given. SQLite assigns the id and
        // never compares the spelling; the in-memory map decides equality. Here we only pin that the table
        // hands out distinct ids and returns the display bytes unchanged, including case.
        using var db = new GatewayStatsDatabase(_path);

        long Insert(string display)
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO repo_identity(repo_display) VALUES ($d); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$d", display);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var first = Insert(@"D:\ReposFred\devthrottle");
        var second = Insert(@"D:\ReposFred\private");

        Assert.NotEqual(first, second);

        using var read = db.Connection.CreateCommand();
        read.CommandText = "SELECT repo_display FROM repo_identity WHERE repo_id=$i";
        read.Parameters.AddWithValue("$i", first);
        // Verbatim, including the exact casing the Director reported it with.
        Assert.Equal(@"D:\ReposFred\devthrottle", read.ExecuteScalar() as string);
    }

    [Fact]
    public void ArchiveMarker_CannotCollideWithARealHourKey()
    {
        // The archive marker shares the hour_utc column with real hour keys, so it must be impossible for a
        // real key to equal it. Real keys are "yyyy-MM-ddTHH" (GatewayInputStatsAggregator.cs:40); the
        // marker is not parseable as one.
        Assert.False(DateTime.TryParseExact(
            GatewayStatsDatabase.ArchiveMarker,
            "yyyy-MM-ddTHH",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out _));
    }

    [Fact]
    public void Open_UnusableFile_FailsLoudlyAndNamesThePath()
    {
        // No fallback to the JSON store, ever: a database that will not open is a loud failure naming the
        // file, not a silent empty start. A directory where the file should be is a cheap way to make the
        // open fail for real rather than by mocking it.
        var blocked = Path.Combine(_dir, "blocked.db");
        Directory.CreateDirectory(blocked);

        var ex = Assert.Throws<InvalidOperationException>(() => new GatewayStatsDatabase(blocked));

        Assert.Contains(blocked, ex.Message);
        Assert.Contains("will not fall back", ex.Message);
    }

    // ---- A hand-built PRE-SLUG store, used to exercise the schema v4 reset. ----
    //
    // This writes the exact shape a PREVIOUS BUILD wrote: stat_delta without model_id or checkout_id, no
    // model_identity, repo_id keyed by a local PATH, user_version=1. Until v4 the contract was that such a
    // file migrated forward and kept every row; at v4 the repository dimension changed meaning (path became
    // GitHub slug) and a path-keyed row can no longer be re-keyed, so the owner ruled it is not carried
    // across - the file is retired aside unread and the store starts empty. These rows are therefore what a
    // real pre-slug store looks like on disk, so the retire tests can prove it is retired (not migrated) and
    // that the retired copy stays intact.
    private void WriteVersion1Database(params (string Hour, long Turns, long Chars)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        void Run(string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Run(@"CREATE TABLE stat_delta (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  hour_utc   TEXT    NOT NULL,
                  session_id TEXT    NOT NULL,
                  modality   TEXT    NOT NULL,
                  surface    TEXT    NOT NULL,
                  is_voice   INTEGER NOT NULL,
                  repo_id    INTEGER NOT NULL,
                  wingman    INTEGER NOT NULL,
                  turns      INTEGER NOT NULL,
                  chars      INTEGER NOT NULL)");
        Run("CREATE TABLE session_highwater (session_id TEXT NOT NULL, modality TEXT NOT NULL, surface TEXT NOT NULL, turns INTEGER NOT NULL, chars INTEGER NOT NULL, PRIMARY KEY (session_id, modality, surface))");
        Run("CREATE TABLE wingman_session (session_id TEXT PRIMARY KEY)");
        Run("CREATE TABLE repo_session (repo_id INTEGER NOT NULL, session_id TEXT NOT NULL, PRIMARY KEY (repo_id, session_id))");
        Run("CREATE TABLE agent_session (agent_id INTEGER NOT NULL, session_id TEXT NOT NULL, PRIMARY KEY (agent_id, session_id))");
        Run("CREATE TABLE repo_identity (repo_id INTEGER PRIMARY KEY AUTOINCREMENT, repo_display TEXT NOT NULL)");
        Run("CREATE TABLE agent_identity (agent_id INTEGER PRIMARY KEY AUTOINCREMENT, agent_display TEXT NOT NULL)");
        Run("CREATE TABLE agent_delta (id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id INTEGER NOT NULL, is_voice INTEGER NOT NULL, turns INTEGER NOT NULL, chars INTEGER NOT NULL)");
        Run("CREATE TABLE agent_driven_delta (id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id INTEGER NOT NULL, turns INTEGER NOT NULL, chars INTEGER NOT NULL)");
        Run("CREATE TABLE agent_driven_highwater (session_id TEXT PRIMARY KEY, turns INTEGER NOT NULL, chars INTEGER NOT NULL)");
        Run("CREATE TABLE agents_seeded (session_id TEXT PRIMARY KEY)");
        Run("CREATE TABLE meta (name TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Run("INSERT INTO repo_identity(repo_display) VALUES ('D:\\ReposFred\\devthrottle')");

        foreach (var (hour, turns, chars) in rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars)
                                VALUES ($h, 'sess-1', 'typed', 'desktop', 0, 1, 0, $t, $c)";
            cmd.Parameters.AddWithValue("$h", hour);
            cmd.Parameters.AddWithValue("$t", turns);
            cmd.Parameters.AddWithValue("$c", chars);
            cmd.ExecuteNonQuery();
        }

        Run("PRAGMA user_version=1");
        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    private static long ScalarLong(GatewayStatsDatabase db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Open_PreSlugStore_IsRetiredAsideUnread_AndStartsEmpty()
    {
        // The repository dimension changed meaning at schema v4: repo_id was the session's local path and is
        // now its GitHub slug. A stored path-keyed row cannot be re-keyed to a slug (the Gateway has no
        // filesystem to resolve the path), so - as with the legacy JSON store, and by the same owner ruling -
        // a pre-slug store (any version 1..3) is not migrated forward. It is renamed aside UNREAD and this
        // store starts empty. This is the reset the owner approved ("we're not really live yet").
        WriteVersion1Database(("2026-07-16T09", 40, 400), ("2026-07-16T10", 27, 270));

        using var db = new GatewayStatsDatabase(_path);

        // The fresh store is at the current version and carries NONE of the pre-slug rows.
        Assert.Equal(GatewayStatsDatabase.SchemaVersion, UserVersion(db));
        Assert.Equal(0, ScalarLong(db, "SELECT COUNT(*) FROM stat_delta"));
        Assert.Equal(0, ScalarLong(db, "SELECT COUNT(*) FROM repo_identity"));
        // The new dimension is present on the fresh store.
        Assert.True(TableExists(db, "checkout_identity"));
        Assert.Contains("checkout_id", ColumnsOf(db, "stat_delta"));

        // Renamed, never deleted - the old numbers are recoverable, which was the owner's line on the JSON
        // store too. Exactly one retired copy sits beside the fresh database.
        var retired = Directory.GetFiles(_dir, "gateway-stats.db.pre-slug-*");
        Assert.Single(retired);
    }

    [Fact]
    public void Open_PreSlugStore_LeavesTheRetiredCopyIntactAndReadable()
    {
        // "Renamed aside UNREAD" must mean the bytes are untouched: the retired file still holds the old rows,
        // so the reset is recoverable rather than a destructive wipe.
        WriteVersion1Database(("2026-07-16T09", 40, 400), ("2026-07-16T10", 27, 270));

        using (var db = new GatewayStatsDatabase(_path)) { /* triggers the retire */ }
        SqliteConnection.ClearAllPools();

        var retired = Directory.GetFiles(_dir, "gateway-stats.db.pre-slug-*").Single();
        using var old = new SqliteConnection($"Data Source={retired}");
        old.Open();
        using var cmd = old.CreateCommand();
        cmd.CommandText = "SELECT SUM(turns) FROM stat_delta";
        Assert.Equal(67L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Open_CurrentVersionStore_IsNotRetired()
    {
        // The retire is one-time and version-gated: a store already at the current version is current, so a
        // reopen must leave it exactly in place and mint no retired copy. This is what makes the retire
        // self-idempotent - the fresh v4 store this build writes is never retired on the next startup.
        using (var first = new GatewayStatsDatabase(_path))
        {
            using var cmd = first.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(name, value) VALUES ('probe', 'kept')";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var second = new GatewayStatsDatabase(_path);
        Assert.Empty(Directory.GetFiles(_dir, "gateway-stats.db.pre-slug-*"));
        using var read = second.Connection.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE name='probe'";
        Assert.Equal("kept", read.ExecuteScalar() as string);
    }

    [Fact]
    public void TokenDelta_CarriesSpendScalarsAndModel_ButNeverContextOccupancy()
    {
        // The schema itself enforces spend-not-occupancy: the four columns are the cumulative, additive token
        // counts plus the nullable model, and NOTHING else. A context-occupancy column here would be a gauge
        // that lies the moment a SUM is taken over it, so its absence is the design. An exhaustive whitelist,
        // not a forbidden-name list, so any new column fails here until stated deliberately.
        using var db = new GatewayStatsDatabase(_path);

        var columns = ColumnsOf(db, "token_delta");
        var expected = new[]
        {
            "id", "hour_utc", "model_id",
            "input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens",
        };
        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal),
                     columns.OrderBy(c => c, StringComparer.Ordinal));

        // No modality, no surface: tokens are the model's work, not the human's input channel.
        Assert.DoesNotContain("modality", columns);
        Assert.DoesNotContain("surface", columns);
        // The one gauge on the wire must not have leaked into the summable table.
        Assert.DoesNotContain("context_tokens", columns);
    }

    private static List<string> ColumnsOf(GatewayStatsDatabase db, string table)
    {
        var names = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void Open_FreshStore_StampsWhenTheModelDimensionBegan()
    {
        // The model dimension's since-stamp is written by the version 2 migration, which runs as part of
        // building even a FRESH store (0 -> current), so a brand-new database carries it. A null model_id is
        // unreadable without it: a row that predates the dimension and one whose session had recorded no model
        // yet both store NULL, and only this stamp separates them.
        var before = DateTime.UtcNow.AddSeconds(-1);

        using var db = new GatewayStatsDatabase(_path);

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", GatewayStatsDatabase.ModelsSinceKey);
        var stamped = cmd.ExecuteScalar() as string;

        Assert.False(string.IsNullOrEmpty(stamped));
        Assert.True(DateTime.TryParse(stamped, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var since));
        Assert.True(since >= before, $"models_since_utc '{stamped}' predates the store's creation.");
    }
}
