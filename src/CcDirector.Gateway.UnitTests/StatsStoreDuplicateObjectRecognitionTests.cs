using CcDirector.Gateway.Stats.Data;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A HALF-BUILT STORE IS RECOGNISED WHEN IT HAPPENS, not only predicted in advance.
///
/// WHY PREDICTION ALONE CANNOT BE ENOUGH. The pre-check in <see cref="GatewayStatsStore"/> looks for the
/// half-built state before running the chain, and it can never be COMPLETE about a state left behind by a
/// process that DIED: completeness would mean enumerating every object each pending migration creates, and
/// the next unusual death produces a shape nobody enumerated. So the state is also recognised on failure.
///
/// AND THIS IS THE CASE THAT DEFEATS A CORRECT CLASSIFIER, which is what makes it worth its own file. A
/// duplicate-table error genuinely IS a provider exception, so <see cref="GatewayStatsStore.IsStorageFailure"/>
/// calls it the operator's fault - and it is RIGHT by the rule and WRONG about the world. Left alone it
/// reports UNREACHABLE and sends somebody to check a healthy network over a schema sitting half-built on
/// their own disk. The recogniser has to run BEFORE the boundary ever sees it, and this file pins that both
/// things are true at once: the exception is a storage failure AND it means half-built.
///
/// SQLSTATE AND NOT MESSAGE TEXT. Messages are localised, reworded between server versions and different per
/// object kind; a SQLSTATE is protocol contract. Keying off the code is also what makes this testable with
/// no PostgreSQL server at all - the recogniser is exercised with real <see cref="PostgresException"/>
/// instances carrying real codes, so it is watched working rather than assumed to work until someone stands
/// a server up.
/// </summary>
public sealed class StatsStoreDuplicateObjectRecognitionTests
{
    private readonly ITestOutputHelper _out;

    public StatsStoreDuplicateObjectRecognitionTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every duplicate-object SQLSTATE the migration chain can realistically hit, recognised and
    /// NAMED. The names are in the log line an operator reads, so a wrong one is a wrong diagnosis.</summary>
    [Theory]
    [InlineData("42P07", "duplicate_table")]
    [InlineData("42701", "duplicate_column")]
    [InlineData("42710", "duplicate_object")]
    [InlineData("42P06", "duplicate_schema")]
    [InlineData("42723", "duplicate_function")]
    [InlineData("42P04", "duplicate_database")]
    public void ADuplicateObjectSqlState_IsRecognisedAndNamed(string sqlState, string expectedName)
    {
        var thrown = Postgres(sqlState);

        Assert.True(
            GatewayStatsStore.IsPostgresDuplicateObjectFailure(thrown, out var signal),
            $"SQLSTATE {sqlState} was not recognised as a duplicate object, so a half-built store would be " +
            "reported as an unreachable database.");
        Assert.Contains(sqlState, signal, StringComparison.Ordinal);
        Assert.Contains(expectedName, signal, StringComparison.Ordinal);

        _out.WriteLine($"{sqlState} -> {signal}");
    }

    /// <summary>
    /// THE OTHER FAILURE DIRECTION. A recogniser that said yes to everything would turn every genuine
    /// database problem into "your schema is half built" - so the codes that mean a REAL fault must not be
    /// recognised. These are the ones a statistics migration would actually meet against a live server.
    /// </summary>
    [Theory]
    [InlineData("42501")]  // insufficient_privilege - the restricted role cannot create
    [InlineData("3D000")]  // invalid_catalog_name - the database is not there
    [InlineData("28P01")]  // invalid_password
    [InlineData("53300")]  // too_many_connections
    [InlineData("42P01")]  // undefined_table - the opposite problem entirely
    [InlineData("57014")]  // query_canceled
    public void AnOrdinaryDatabaseFault_IsNotMistakenForAHalfBuiltStore(string sqlState)
    {
        Assert.False(
            GatewayStatsStore.IsPostgresDuplicateObjectFailure(Postgres(sqlState), out _),
            $"SQLSTATE {sqlState} was mistaken for a duplicate object, so a real database fault would be " +
            "reported to the operator as a half-built schema and they would go looking at their disk.");
    }

    /// <summary>
    /// Entity Framework wraps provider exceptions while migrating, so the chain is walked. A recogniser that
    /// only looked at the outermost exception would miss every duplicate raised during an actual migration -
    /// which is the only time this code ever runs.
    /// </summary>
    [Fact]
    public void ADuplicateWrappedByEntityFramework_IsStillRecognised()
    {
        var wrapped = new InvalidOperationException(
            "An error occurred while applying migrations.", Postgres("42P07"));

        Assert.True(
            GatewayStatsStore.IsPostgresDuplicateObjectFailure(wrapped, out var signal),
            "A wrapped duplicate-table failure was not recognised, and wrapped is how it actually arrives.");
        Assert.Contains("42P07", signal, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAPostgresFailureAtAll_IsNotRecognised()
    {
        Assert.False(GatewayStatsStore.IsPostgresDuplicateObjectFailure(new NullReferenceException(), out _));
        Assert.False(GatewayStatsStore.IsPostgresDuplicateObjectFailure(null, out _));
    }

    /// <summary>
    /// THE TRAP THIS WHOLE MECHANISM EXISTS FOR, pinned so it cannot be quietly undone: a duplicate-table
    /// failure is BOTH a storage failure by the classifier's rule AND a half-built store in fact. Both
    /// statements are true at once, and that is exactly why the recogniser must run before the boundary -
    /// the classifier is not wrong here, it is answering a different question correctly.
    ///
    /// If someone later "simplifies" this by deleting the recogniser and trusting the classifier, this test
    /// fails and says why.
    /// </summary>
    [Fact]
    public void ADuplicateTable_IsBothAStorageFailureByTheRule_AndAHalfBuiltStoreInFact()
    {
        var duplicate = Postgres("42P07");

        Assert.True(
            GatewayStatsStore.IsStorageFailure(duplicate),
            "A PostgresException stopped being classified as a storage failure, which changes what the " +
            "boundary would report if the duplicate recogniser ever stopped running first.");

        Assert.True(
            GatewayStatsStore.IsPostgresDuplicateObjectFailure(duplicate, out _),
            "The duplicate recogniser no longer catches a duplicate table, so this failure would fall " +
            "through to the classifier and be reported as an unreachable database.");
    }

    /// <summary>A real Npgsql exception carrying a chosen SQLSTATE - the type the server actually produces,
    /// not a stand-in, so the property being read is the one that exists at run time.</summary>
    private static PostgresException Postgres(string sqlState) =>
        new(messageText: "relation already exists", severity: "ERROR", invariantSeverity: "ERROR",
            sqlState: sqlState);
}
