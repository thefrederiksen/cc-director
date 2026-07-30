using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// OUTPUT PARITY, on SQLite, between the JSON store
/// (<see cref="Gateway.Stats.GatewaySessionConcurrencyStats"/>, which writes
/// <c>gateway-concurrency-stats.json</c> to the shared file share) and the database store
/// (<see cref="Gateway.Stats.GatewaySessionConcurrencyStore"/>, which replaces it).
///
/// One fixture is driven through BOTH implementations, observation for observation, and what is compared is
/// the RENDERED snapshot - the exact JSON body the <c>/stats/data</c> route serves for its
/// <c>concurrency</c> property. That is deliberate, and it is not the same claim as "the same numbers are
/// stored": storing equal numbers and rendering an equal page are two different properties, and only the
/// second is what the owner sees. A difference in a null timestamp, a DateTime Kind, the order of the hourly
/// list, or an hour bucket that one store creates and the other does not, all show up here and none of them
/// would show up in a row-by-row comparison of the two stores' contents.
///
/// The fixture and the comparison live in <see cref="ConcurrencyStoreScenarios"/> and are run against real
/// PostgreSQL too, in <see cref="GatewaySessionConcurrencyPostgresTests"/>.
///
/// SCOPE, stated rather than implied. The fixture moves forward in time, because production time does. If an
/// observation ever arrived for an hour EARLIER than one already folded, the two implementations would
/// diverge - the JSON store cleared its dedup sets and started that hour's distinct counts again from
/// nothing, whereas the database store rehydrates that hour's members and carries on counting. The database
/// store is the more accurate of the two there; it is called out because this test does not cover it.
/// </summary>
public sealed class GatewaySessionConcurrencyParityTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();
    private readonly string _jsonPath =
        Path.Combine(Path.GetTempPath(), "cc-conc-parity-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_jsonPath); } catch (IOException) { /* temp artifact */ }
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_AcrossTheWholeFixture_AndAfterBothStoresRestart()
    {
        ConcurrencyStoreScenarios.AssertOutputParityAcrossTheFixture(() => _db.NewFactory(), _jsonPath);
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_OnTheRetentionBoundary()
    {
        ConcurrencyStoreScenarios.AssertOutputParityOnTheRetentionBoundary(
            () => _db.NewFactory(), _jsonPath, TenantId.Local);
    }

    [Fact]
    public void TheParityComparison_NoticesWhenTheTwoStoresDiverge()
    {
        // Without this, every green above is consistent with a comparison that can never fail.
        ConcurrencyStoreScenarios.AssertTheParityComparisonDetectsADifference(
            () => _db.NewFactory(), _jsonPath, TenantId.Local);
    }
}
