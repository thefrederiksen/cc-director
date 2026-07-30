using System.Text;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE PRECONDITION THE WHOLE ADOPTION STEP RESTS ON: a database built by RUNNING the shipped
/// <see cref="GatewayStatsDatabase"/> and a database built by running the new Entity Framework baseline
/// migration are STRUCTURALLY THE SAME DATABASE.
///
/// Why this outranks the adoption tests rather than sitting beside them. Adoption is a CLAIM, not a
/// convenience: stamping the baseline as applied TELLS Entity Framework that the file on disk is what that
/// baseline would have produced. If the two schemas differ at all, the file has not been adopted - the
/// framework has been told a lie about it, and every later migration in the chain is then applied to a
/// database that is not the one the chain believes it is operating on.
///
/// That failure does not surface at open time, where the adoption code is. It surfaces LATER, on a real
/// user's machine, as a missing index or an absent constraint, with a stack trace pointing nowhere near
/// adoption. And NO test that starts from a fresh database can ever see it, because both sides of that
/// comparison come from the new chain and agree by construction - a guard supplying its own evidence, which
/// would pass everything else in this suite. Only a comparison against a database made by the OLD code can
/// see it, which is what this is.
///
/// SQLITE ONLY, deliberately. Postgres starts empty on the hosted Gateway; there is no existing file for it
/// to be equivalent to, so there is no problem there to have and this is not made symmetric.
///
/// ANY divergence found here is a DEFECT IN THE BASELINE MIGRATION, not a known difference to note and move
/// past. The fix is to make the baseline reproduce version 5 exactly - which is why its <c>Up()</c> is the
/// literal version 5 DDL rather than a generated approximation of it: equivalence is then true BY
/// CONSTRUCTION, and this comparison is a check that cannot drift rather than a chore that has to keep being
/// re-verified by hand.
/// </summary>
public sealed class GatewayStatsSqliteBaselineEquivalenceTests : IDisposable
{
    private readonly string _dir;

    /// <summary>The database built by running the shipped hand-rolled code - the shape that is on real
    /// self-host machines today.</summary>
    private readonly string _handRolledPath;

    /// <summary>The database built by running the new Entity Framework baseline against an empty file.</summary>
    private readonly string _baselinePath;

    /// <summary>
    /// Entity Framework's OWN bookkeeping tables, and the only things excluded from this comparison.
    ///
    /// <c>__EFMigrationsHistory</c> records which migrations have run - creating it is precisely what
    /// adoption ADDS to a hand-rolled store. <c>__EFMigrationsLock</c> is the advisory lock row Entity
    /// Framework uses to stop two processes migrating at once. Neither is part of the statistics schema, and
    /// neither exists in a hand-rolled store because that path never used Entity Framework.
    ///
    /// This list was not guessed. It is what running the comparison actually reported, which is the point of
    /// running it rather than reasoning about it: the lock table was not in anybody's mental model of the
    /// difference, and would have read as a divergence.
    ///
    /// NOTHING ELSE MAY BE ADDED HERE. Every other difference is a defect in the baseline migration, and the
    /// fix is to make the baseline reproduce version 5 - not to widen this list until the test is quiet.
    /// </summary>
    private static readonly string[] EntityFrameworkBookkeepingTables =
    {
        "__EFMigrationsHistory",
        "__EFMigrationsLock",
    };

    private static bool IsBookkeeping(string name) =>
        EntityFrameworkBookkeepingTables.Contains(name, StringComparer.Ordinal);

    public GatewayStatsSqliteBaselineEquivalenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-equiv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _handRolledPath = Path.Combine(_dir, "hand-rolled.db");
        _baselinePath = Path.Combine(_dir, "baseline.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private void BuildHandRolledDatabase()
    {
        using (var db = new GatewayStatsDatabase(_handRolledPath))
        {
            Assert.Equal(5, GatewayStatsDatabase.SchemaVersion);
        }
        SqliteConnection.ClearAllPools();
    }

    private void BuildBaselineDatabase()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _baselinePath }.ToString())
            .Options;
        using (var context = new GatewayStatsDbContext(options))
        {
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }

    // ---- the comparison ---------------------------------------------------------------------------------

    /// <summary>
    /// The strict half: the CREATE statement SQLite itself stored for every table and index, normalised for
    /// whitespace only.
    ///
    /// Whitespace ONLY. It is tempting to also normalise quoting, casing and bracketing to stop the
    /// comparison being "noisy", but each of those would be a difference this test then cannot see, and the
    /// point of the test is to see differences. Formatting is made equal by writing the baseline as the
    /// literal version 5 DDL, not by teaching the comparison to ignore it.
    /// </summary>
    private static SortedDictionary<string, string> StoredSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL";

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (IsBookkeeping(name)) continue;
            map[$"{reader.GetString(0)} {name}"] = Normalise(reader.GetString(2));
        }
        return map;
    }

    private static string Normalise(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var lastWasSpace = false;
        foreach (var c in sql)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(c);
            lastWasSpace = false;
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// The semantic half, read from the PRAGMAs rather than from text: for every table, its columns in
    /// declaration ORDER with their type, NULLABILITY, default and PRIMARY KEY POSITION; and every index on
    /// it by NAME with its UNIQUENESS, origin and column order. This is what "the same database" actually
    /// means, and it catches a difference that happens to be spelled the same way as well as one that is
    /// spelled differently.
    ///
    /// Automatically-created indexes are INCLUDED. Their names are derived by SQLite from how the key was
    /// declared, so they are real evidence about the declaration rather than noise.
    /// </summary>
    private static SortedDictionary<string, string> Structure(SqliteConnection connection)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var table in TableNames(connection))
        {
            var lines = new List<string>();

            foreach (var row in Query(connection, $"PRAGMA table_info(\"{table}\")"))
                lines.Add($"column cid={row["cid"]} name={row["name"]} type={row["type"]} " +
                          $"notnull={row["notnull"]} default={row["dflt_value"] ?? "<none>"} pk={row["pk"]}");

            foreach (var index in Query(connection, $"PRAGMA index_list(\"{table}\")")
                         .OrderBy(r => r["name"], StringComparer.Ordinal))
            {
                var name = index["name"];
                var columns = Query(connection, $"PRAGMA index_info(\"{name}\")")
                    .Select(r => $"{r["seqno"]}:{r["name"]}");
                lines.Add($"index name={name} unique={index["unique"]} origin={index["origin"]} " +
                          $"partial={index["partial"]} columns=[{string.Join(", ", columns)}]");
            }

            map[table] = string.Join("\n", lines);
        }

        return map;
    }

    private static List<string> TableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!IsBookkeeping(name)) names.Add(name);
        }
        return names;
    }

    private static List<Dictionary<string, string?>> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<Dictionary<string, string?>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            rows.Add(row);
        }
        return rows;
    }

    private static int UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ---- refuse a fixture that cannot fail ---------------------------------------------------------------

    /// <summary>
    /// REFUSE A COMPARISON THAT COULD NOT SHOW A DIFFERENCE, before trusting anything it says.
    ///
    /// This whole test is two dictionaries compared for equality, and two EMPTY dictionaries are equal. If
    /// the hand-rolled fixture failed to build, or a query returned nothing, or the exclusion list quietly
    /// swallowed the schema, every assertion below would pass - reporting equivalence between two databases
    /// it never actually looked at. That is the most comfortable false green available here, and it needs
    /// refusing rather than remembering.
    ///
    /// So the fixture must be shown to CONTAIN the thing a divergence would appear in: the sixteen tables and
    /// the four named indexes a real version 5 store carries. The check does not ask anyone to be careful; it
    /// rejects a comparison that cannot fail.
    /// </summary>
    private static void AssertTheComparisonCouldShowADifference(
        SortedDictionary<string, string> handRolledObjects)
    {
        var tables = handRolledObjects.Keys.Count(k => k.StartsWith("table ", StringComparison.Ordinal));
        var indexes = handRolledObjects.Keys.Count(k => k.StartsWith("index ", StringComparison.Ordinal));

        Assert.True(tables == 16 && indexes == 4,
            $"The hand-rolled version 5 fixture holds {tables} table(s) and {indexes} named index(es), but a " +
            "real version 5 store has 16 and 4. This comparison is therefore not looking at the schema it " +
            "claims to compare, and any equality it reports is vacuous. Fix the fixture - do not trust the " +
            "green.");
    }

    // ---- the assertions ---------------------------------------------------------------------------------

    [Fact]
    public void Baseline_CreatesTheSameTablesAndIndexesAsTheHandRolledVersion5Code()
    {
        BuildHandRolledDatabase();
        BuildBaselineDatabase();

        using var handRolled = Open(_handRolledPath);
        using var baseline = Open(_baselinePath);

        var expected = StoredSql(handRolled);
        var actual = StoredSql(baseline);

        AssertTheComparisonCouldShowADifference(expected);

        // The object SET first, so a missing or extra table or index is reported as exactly that rather than
        // as a confusing text difference on some unrelated object.
        Assert.Equal(expected.Keys.ToArray(), actual.Keys.ToArray());

        // Then object by object, so the failure NAMES the one that diverged.
        foreach (var (key, expectedSql) in expected)
            Assert.Equal($"{key} => {expectedSql}", $"{key} => {actual[key]}");
    }

    [Fact]
    public void Baseline_ProducesTheSameColumnsKeysAndIndexStructureAsTheHandRolledVersion5Code()
    {
        BuildHandRolledDatabase();
        BuildBaselineDatabase();

        using var handRolled = Open(_handRolledPath);
        using var baseline = Open(_baselinePath);

        var expected = Structure(handRolled);
        var actual = Structure(baseline);

        // Same refusal as above, in the terms this map is keyed by: sixteen tables, each with columns read
        // back. An empty structure map would compare equal to another empty one and prove nothing.
        Assert.True(expected.Count == 16 && expected.Values.All(v => v.Contains("column ", StringComparison.Ordinal)),
            $"The hand-rolled version 5 fixture yielded {expected.Count} table structure(s), and every one " +
            "must list its columns. A real version 5 store has 16. This comparison cannot show a difference " +
            "in a schema it did not read, so any equality it reports is vacuous.");

        Assert.Equal(expected.Keys.ToArray(), actual.Keys.ToArray());

        foreach (var (table, expectedStructure) in expected)
            Assert.Equal($"{table}:\n{expectedStructure}", $"{table}:\n{actual[table]}");
    }

    /// <summary>
    /// The version stamp is part of the shape too. A hand-rolled store reports 5; a baseline-built store must
    /// report 5 as well, or an older build meeting a newer file would read 0, decide the file predates every
    /// migration, and run its version 1 to 5 steps against tables that already exist.
    /// </summary>
    [Fact]
    public void Baseline_StampsTheSameSchemaVersionAsTheHandRolledVersion5Code()
    {
        BuildHandRolledDatabase();
        BuildBaselineDatabase();

        using var handRolled = Open(_handRolledPath);
        using var baseline = Open(_baselinePath);

        Assert.Equal(GatewayStatsDatabase.SchemaVersion, UserVersion(handRolled));
        Assert.Equal(UserVersion(handRolled), UserVersion(baseline));
    }
}
