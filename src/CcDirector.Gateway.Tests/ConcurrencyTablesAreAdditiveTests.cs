using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Where the concurrency store's three tables stand in relation to the statistics database that already
/// exists on every self-host machine - checked against a file produced by RUNNING the real
/// <see cref="GatewayStatsDatabase"/>, not against anything derived from the Entity Framework model.
///
/// WHY THIS EXISTS. A test fixture built from the model is a guard that supplies its own evidence: the model
/// and the fixture agree by construction, so the test passes just as happily when both are wrong together.
/// That matters on this step because the SQLite baseline migration is being written as the literal schema
/// version 5 DDL rather than generated from the model, so the two CAN drift, and the drift would surface on
/// a user's machine as a query error rather than on ours as a migration error.
///
/// For these three tables specifically the answer is that there is nothing yet to drift FROM, and this test
/// is what makes that a measured fact instead of an assumption: <c>concurrency_peak</c>,
/// <c>concurrency_hour</c> and <c>concurrency_hour_member</c> do not exist at version 5 in any form. The
/// concurrency record was a JSON file, not a table. So the port is purely additive, and the first authority
/// on these tables' shape will be the migration that creates them - at which point the model-built fixtures
/// in the other concurrency suites must be rebuilt on that migration, exactly as this note says.
/// </summary>
public sealed class ConcurrencyTablesAreAdditiveTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "cc-stats-v5-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly List<ServiceProvider> _providers = new();

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { /* temp artifact */ }
    }

    private IDbContextFactory<GatewayStatsDbContext> FactoryForTheRealFile()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayStatsDbContext>(o =>
            o.UseSqlite(new SqliteConnectionStringBuilder { DataSource = _path }.ToString()));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
    }

    private List<string> TableNamesInTheRealFile()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void ARealVersionFiveStore_HasNoneOfTheThreeConcurrencyTables()
    {
        // The real thing, run: this is the file shape every self-host user who has opened the statistics page
        // already has on disk.
        using (var real = new GatewayStatsDatabase(_path))
        {
            Assert.Equal(5, GatewayStatsDatabase.SchemaVersion);
        }

        var tables = TableNamesInTheRealFile();

        // The version 5 tables are there ...
        Assert.Contains("stat_delta", tables);
        Assert.Contains("session_highwater", tables);
        Assert.Contains("repo_identity", tables);

        // ... and none of the three this port adds, in any spelling. There is therefore no existing on-disk
        // shape for these three to be measured against, and the migration that creates them will be their
        // first authority.
        Assert.DoesNotContain("concurrency_peak", tables);
        Assert.DoesNotContain("concurrency_hour", tables);
        Assert.DoesNotContain("concurrency_hour_member", tables);
    }

    [Fact]
    public void TheStore_FailsLoudAgainstAVersionFiveFile_RatherThanRecordingNothing()
    {
        using (var real = new GatewayStatsDatabase(_path)) { }

        // Pointed at a statistics database that predates its tables, the store must fail and name the table
        // it wanted. The failure mode that would actually hurt is the quiet one - a store that swallowed the
        // missing table and reported zeroes would put an empty concurrency chart in front of the owner and
        // give nobody a reason to look.
        var store = new GatewaySessionConcurrencyStore(FactoryForTheRealFile());
        var ex = Assert.Throws<SqliteException>(() =>
            store.Observe(ConcurrencyStoreScenarios.Roster(3, 1), ConcurrencyStoreScenarios.T0));
        Assert.Contains("concurrency_peak", ex.Message, StringComparison.Ordinal);
    }
}
