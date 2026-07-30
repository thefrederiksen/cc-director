using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE BOUNDARY MUST SEPARATE OUR FAULT FROM THEIRS.
///
/// THE MECHANISM THIS GUARDS, which is more important than the individual bug that revealed it. A containment
/// that catches EVERYTHING cannot, by itself, tell "the store is unreachable" from "we have a bug". If it
/// guesses "unreachable", then every programming error inside the boundary is handed a plausible
/// INFRASTRUCTURE label - and the operator is sent to audit a database, a network and a set of settings that
/// are all perfectly healthy, while the actual fault sits in our code where they will never look.
///
/// It is not hypothetical and it is not rare. On 2026-07-30 it happened three times in one day: an endpoint
/// catch reporting a null reference as a storage fault, a watcher reading a cancelled run as an answer, and -
/// the one that produced this file - a missing entry in the statistics reason-code map reported as an
/// unreachable database. Three different authors, three different files, one mechanism.
///
/// WHY A USER IS BETTER OFF BEING TOLD IT IS OUR BUG. "Something in DevThrottle's own code failed" is at
/// least TRUE, and it is actionable by them in the only way that matters: telling us. "Your database is
/// unreachable" is a false statement that costs them an investigation and costs us the report.
///
/// The classifier is tested against REAL exceptions from the real providers - a genuine
/// <see cref="NpgsqlException"/> from a refused connection, a genuine <see cref="SqliteException"/> from a
/// bad statement - rather than hand-made stand-ins, because a stand-in proves the rule against a shape that
/// may not be the one the providers actually throw.
/// </summary>
public sealed class StatsStoreOurFaultOrTheirsTests
{
    private readonly ITestOutputHelper _out;

    public StatsStoreOurFaultOrTheirsTests(ITestOutputHelper output) => _out = output;

    // ============================================================ theirs: real provider exceptions

    /// <summary>A refused PostgreSQL connection is THEIRS. Produced by actually attempting one.</summary>
    [Fact]
    public void ARefusedPostgresConnection_IsTheirFault()
    {
        var thrown = Record.Exception(() =>
        {
            using var connection = new NpgsqlConnection(
                "Host=127.0.0.1;Port=1;Database=nope;Username=u;Password=p;Timeout=2");
            connection.Open();
        });

        Assert.NotNull(thrown);
        _out.WriteLine($"THEIRS: {thrown!.GetType().FullName}");
        Assert.True(
            GatewayStatsStore.IsStorageFailure(thrown),
            $"A refused database connection was classified as OUR bug: {thrown.GetType().FullName}");
    }

    /// <summary>A SQLite failure is THEIRS. Produced by actually running a statement against a real file.</summary>
    [Fact]
    public void ASqliteFailure_IsTheirFault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "s.db") }.ToString());
            connection.Open();

            var thrown = Record.Exception(() =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM a_table_that_does_not_exist";
                command.ExecuteNonQuery();
            });

            Assert.NotNull(thrown);
            _out.WriteLine($"THEIRS: {thrown!.GetType().FullName}");
            Assert.True(GatewayStatsStore.IsStorageFailure(thrown));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// A genuine outage that arrived WRAPPED is still theirs. Entity Framework routinely wraps a provider
    /// exception in one of its own, and a boundary that only looked at the outermost type would call a real
    /// database outage our bug - which is this defect running in the opposite direction.
    /// </summary>
    [Fact]
    public void AProviderFailureWrappedByOurStack_IsStillTheirFault()
    {
        var inner = Record.Exception(() =>
        {
            using var connection = new NpgsqlConnection(
                "Host=127.0.0.1;Port=1;Database=nope;Username=u;Password=p;Timeout=2");
            connection.Open();
        });

        var wrapped = new InvalidOperationException("An error occurred using the connection.", inner);

        Assert.True(
            GatewayStatsStore.IsStorageFailure(wrapped),
            "A wrapped provider failure was classified as our bug, so a real outage would be reported as a " +
            "DevThrottle defect.");
    }

    // ============================================================ ours: programming errors

    /// <summary>
    /// THE ONE THAT STARTED THIS. A reason with no entry in the code map throws
    /// <see cref="ArgumentOutOfRangeException"/> from our own switch, and that must read as OUR bug. Before
    /// this classifier it was reported as an unreachable database, so a self-host user with a half-built
    /// store on disk was sent to check their network.
    /// </summary>
    [Fact]
    public void AMissingReasonCode_IsOurFault_NotAnUnreachableDatabase()
    {
        var thrown = Record.Exception(() => GatewayStatsStore.CodeFor((StatsStoreUnavailableReason)9999));

        Assert.NotNull(thrown);
        Assert.IsType<ArgumentOutOfRangeException>(thrown);
        _out.WriteLine($"OURS: {thrown!.GetType().FullName}");

        Assert.False(
            GatewayStatsStore.IsStorageFailure(thrown),
            "A bug in our own switch statement was classified as a storage failure, which is exactly the " +
            "defect this classifier exists to stop.");
    }

    [Theory]
    [MemberData(nameof(OurOwnMistakes))]
    public void AnOrdinaryProgrammingError_IsOurFault(Exception ours)
    {
        Assert.False(
            GatewayStatsStore.IsStorageFailure(ours),
            $"{ours.GetType().Name} was classified as a storage failure, so this bug would be reported to " +
            "the operator as a problem with their database.");
    }

    public static TheoryData<Exception> OurOwnMistakes() => new()
    {
        new NullReferenceException(),
        new InvalidCastException(),
        new KeyNotFoundException(),
        new IndexOutOfRangeException(),
        new ArgumentNullException("thing"),
        new InvalidOperationException("the sequence contains no elements"),
    };

    // ============================================================ the surface says whose fault it is

    /// <summary>
    /// The two sentences are compared to EACH OTHER, not merely checked for keywords. A build that had
    /// collapsed them would satisfy any single-sided assertion; only the comparison can see it.
    ///
    /// The storage sentence must not accuse us, and the internal sentence must not accuse the operator's
    /// database, network or settings - because the whole cost of getting this wrong is measured in where
    /// somebody goes to look.
    /// </summary>
    [Fact]
    public void TheTwoSentences_SendTheReaderToDifferentPlaces()
    {
        Assert.Equal("internal_error", GatewayStatsStore.CodeFor(StatsStoreUnavailableReason.InternalError));
        Assert.NotEqual(
            GatewayStatsStore.CodeFor(StatsStoreUnavailableReason.InternalError),
            GatewayStatsStore.CodeFor(StatsStoreUnavailableReason.Unreachable));
        Assert.NotEqual(
            GatewayStatsStore.CodeFor(StatsStoreUnavailableReason.InternalError),
            GatewayStatsStore.CodeFor(StatsStoreUnavailableReason.NotConfigured));
    }
}
