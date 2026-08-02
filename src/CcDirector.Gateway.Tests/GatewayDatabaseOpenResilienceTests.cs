using CcDirector.Gateway.Data;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two pure helpers behind the deploy-outage fix (issue #2383).
///
/// On 2 August 2026 a deploy stopped the live site for 38.5 seconds: the container App Service starts
/// after a swap got one refused PostgreSQL connection, treated it as terminal, never bound its port, and
/// the platform stopped the site - killing the healthy container that was serving. The root cause is
/// still unknown because the only thing recorded was the word "PostgresException".
///
/// These pin the two properties that make that not happen again and make the next one readable:
///   - the connection pool is bounded, so a swap's four containers cannot exhaust the server, and an
///     operator who set a size on purpose still wins;
///   - a failure describes what the SERVER said, without ever putting the connection string in a log.
/// The retry itself is exercised by its bounded window rather than by a unit test - it needs a refusing
/// server, and standing one up to prove a sleep loop would test the harness, not the fix.
/// </summary>
public sealed class GatewayDatabaseOpenResilienceTests
{
    private const string BareConnection = "Host=db.example.com;Database=postgres;Username=u;Password=p";

    [Fact]
    public void WithBoundedPool_CapsAConnectionStringThatSaysNothingAboutPooling()
    {
        // Npgsql's default is 100 per pool, and a Gateway container runs two pools. Unbounded, a swap's
        // four containers ask for far more than the sixty the server allows.
        var bounded = new NpgsqlConnectionStringBuilder(
            GatewayDatabase.WithBoundedPool(BareConnection, GatewayDatabase.DefaultMaxPoolSize));

        Assert.Equal(GatewayDatabase.DefaultMaxPoolSize, bounded.MaxPoolSize);
    }

    [Fact]
    public void WithBoundedPool_KeepsEverythingElseAboutTheConnection()
    {
        // Capping the pool must not quietly rewrite where we connect or as whom.
        var bounded = new NpgsqlConnectionStringBuilder(
            GatewayDatabase.WithBoundedPool(BareConnection, GatewayDatabase.DefaultMaxPoolSize));

        Assert.Equal("db.example.com", bounded.Host);
        Assert.Equal("postgres", bounded.Database);
        Assert.Equal("u", bounded.Username);
        Assert.Equal("p", bounded.Password);
    }

    [Theory]
    [InlineData("Maximum Pool Size=42")]
    [InlineData("MaxPoolSize=42")]
    public void WithBoundedPool_LeavesAnOperatorsOwnPoolSizeAlone(string poolClause)
    {
        // This is a ceiling for the unconfigured case, not an override. Someone who sized the pool
        // deliberately - in either accepted spelling - keeps their number.
        var stated = $"{BareConnection};{poolClause}";

        var result = new NpgsqlConnectionStringBuilder(
            GatewayDatabase.WithBoundedPool(stated, GatewayDatabase.DefaultMaxPoolSize));

        Assert.Equal(42, result.MaxPoolSize);
    }

    [Fact]
    public void DescribeFailure_ReportsWhatTheServerActuallySaid()
    {
        // The whole root cause of the 2 August outage is unknowable because only the type name was
        // logged. SqlState and MessageText are PostgreSQL's own code and text for the error.
        var ex = new PostgresException(
            messageText: "remaining connection slots are reserved",
            severity: "FATAL",
            invariantSeverity: "FATAL",
            sqlState: "53300");

        var described = GatewayDatabase.DescribeFailure(ex);

        Assert.Contains("53300", described);
        Assert.Contains("remaining connection slots are reserved", described);
    }

    [Fact]
    public void DescribeFailure_NeverPutsTheConnectionStringInTheLog()
    {
        // The reason only the type name was ever logged: a malformed connection string is echoed back by
        // the parser, so stringifying the exception can leak credentials. That constraint still holds -
        // the fix widened what is logged for a SERVER error only, and this is the guard on that line.
        var leaky = new System.ArgumentException(
            $"Format of the initialization string does not conform: {BareConnection}");

        var described = GatewayDatabase.DescribeFailure(leaky);

        Assert.DoesNotContain("Password", described);
        Assert.DoesNotContain("db.example.com", described);
        Assert.Equal(nameof(System.ArgumentException), described);
    }
}
