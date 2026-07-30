using CcDirector.Gateway.Stats.Data;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The statistics store's CONNECTION CHOICE: which store is selected, what is derived and what is never
/// derived, and - the ruling this file exists for - that NOT CONFIGURED and UNREACHABLE are two different
/// named reasons rather than one failure wearing two labels.
///
/// These are pure: no environment variable, no database, no file. <see cref="StatsConnectionSelection"/>
/// takes the two raw values and the deployment as arguments precisely so that the decision can be tested
/// without a process-wide side effect, and so a test cannot pass because some OTHER test happened to leave
/// a variable set.
/// </summary>
public sealed class StatsConnectionSelectionTests
{
    private const string GatewayConnection =
        "Host=db.example.com;Port=6543;Database=gateway_live;Username=gateway_app;Password=s3cret;SSL Mode=Require";

    private const string SqlitePath = @"C:\storage\gateway-stats.db";

    // ================================================================ the override wins outright

    [Fact]
    public void Resolve_OverrideSet_WinsOutrightAndNothingIsDerived()
    {
        const string explicitConnection = "Host=stats.example.com;Database=stats_only;Username=u;Password=p";

        var choice = StatsConnectionSelection.Resolve(
            statsOverride: explicitConnection,
            gatewayConnection: GatewayConnection,
            hosted: true,
            sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.ExplicitOverride, choice.Source);
        Assert.Equal(StatsStoreUnavailableReason.None, choice.Reason);
        Assert.True(choice.IsConfigured);
        Assert.True(choice.IsPostgres);

        // Byte-for-byte what the operator wrote. The Gateway's own connection is present and DIFFERENT, so a
        // build that derived instead of obeying would fail here rather than passing on a coincidence.
        Assert.Equal(explicitConnection, choice.ConnectionString);
        Assert.DoesNotContain("gateway_live", choice.ConnectionString!, StringComparison.Ordinal);
    }

    /// <summary>
    /// SET BUT BLANK is an operator error, not "unset". Somebody meant to name a database and left the value
    /// empty; reading that as unset would silently select a different store than the one being configured.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_OverrideSetButBlank_IsNotConfiguredAndNamesTheVariable(string blank)
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: blank,
            gatewayConnection: GatewayConnection,
            hosted: true,
            sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.NotConfigured, choice.Source);
        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, choice.Reason);
        Assert.False(choice.IsConfigured);
        Assert.Null(choice.ConnectionString);

        // The variable is NAMED. A reason an operator cannot act on is not a named reason.
        Assert.Contains(StatsConnectionSelection.StatsConnectionEnvVar, choice.Detail, StringComparison.Ordinal);

        // And it did NOT quietly fall through to derivation, which is the whole point: a Gateway connection
        // was available and was deliberately not used.
        Assert.False(choice.IsPostgres);
    }

    // ================================================================ derivation

    [Fact]
    public void Resolve_NoOverrideOnPostgres_DerivesFromTheGatewayConnection()
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null,
            gatewayConnection: GatewayConnection,
            hosted: true,
            sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.DerivedFromGatewayDatabase, choice.Source);
        Assert.Equal(StatsStoreUnavailableReason.None, choice.Reason);
        Assert.True(choice.IsPostgres);

        var source = new NpgsqlConnectionStringBuilder(GatewayConnection);
        var derived = new NpgsqlConnectionStringBuilder(choice.ConnectionString);

        // The server and the credentials come across untouched - "statistics live beside the Gateway's own
        // database" is the entire contract.
        Assert.Equal(source.Host, derived.Host);
        Assert.Equal(source.Port, derived.Port);
        Assert.Equal(source.Username, derived.Username);
        Assert.Equal(source.Password, derived.Password);
        Assert.Equal(source.SslMode, derived.SslMode);

        // Its OWN application name and its OWN pool.
        Assert.Equal(StatsConnectionSelection.StatsApplicationName, derived.ApplicationName);
        Assert.Equal(StatsConnectionSelection.StatsMaxPoolSize, derived.MaxPoolSize);
        Assert.Equal(StatsConnectionSelection.StatsMinPoolSize, derived.MinPoolSize);
    }

    /// <summary>
    /// THE POOL-SEPARATION PROPERTY, which is the reason derivation exists at all rather than reuse.
    ///
    /// Npgsql keys its connection POOLS by the connection string. If the derived string were equal to the
    /// Gateway's, both contexts would silently share ONE pool - and the code would still look perfectly
    /// separated, because there would still be two contexts, two schemas and two migration chains. String
    /// inequality is the only thing that makes the separate pool real, so it is asserted directly.
    /// </summary>
    [Fact]
    public void Resolve_DerivedConnection_IsNotTheSameStringAsTheGatewaysSoThePoolIsSeparate()
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: GatewayConnection, hosted: true, sqlitePath: SqlitePath);

        Assert.NotEqual(GatewayConnection, choice.ConnectionString);

        // And not equal after normalisation either - a difference only in whitespace or key order would
        // still be the SAME pool key to Npgsql, so comparing the raw strings alone could pass on a
        // difference that does not separate anything.
        var normalisedGateway = new NpgsqlConnectionStringBuilder(GatewayConnection).ToString();
        Assert.NotEqual(normalisedGateway, choice.ConnectionString);
    }

    /// <summary>
    /// THE DATABASE NAME IS NEVER DERIVED - it is carried, unaltered. A derivation that could quietly point
    /// at a different database is a different feature, and one whose failure mode is writing a tenant's
    /// numbers somewhere nobody is looking.
    ///
    /// The fixture uses several DIFFERENT database names on purpose. A single fixture whose database
    /// happened to be named the same thing the code might substitute could not show a substitution at all.
    /// </summary>
    [Theory]
    [InlineData("gateway_live")]
    [InlineData("postgres")]
    [InlineData("gateway_stats")]
    [InlineData("some_other_database")]
    public void Resolve_Derivation_CarriesTheDatabaseNameUnaltered(string database)
    {
        var gateway = $"Host=db.example.com;Database={database};Username=gateway_app;Password=s3cret";

        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: gateway, hosted: true, sqlitePath: SqlitePath);

        Assert.Equal(database, new NpgsqlConnectionStringBuilder(choice.ConnectionString).Database);
    }

    /// <summary>
    /// A Gateway connection string that cannot be parsed is a CONFIGURATION problem, and is reported as NOT
    /// CONFIGURED rather than as UNREACHABLE. It is fixed by editing a setting; nobody should spend an
    /// incident looking at the network for it.
    /// </summary>
    [Fact]
    public void Resolve_GatewayConnectionUnparseable_IsNotConfiguredRatherThanUnreachable()
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null,
            gatewayConnection: "Host=db;ThisIsNotAKeyword=1",
            hosted: true,
            sqlitePath: SqlitePath);

        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, choice.Reason);
        Assert.NotEqual(StatsStoreUnavailableReason.Unreachable, choice.Reason);
        Assert.Null(choice.ConnectionString);

        // And the message does NOT echo the string back: a malformed connection string is echoed by the
        // parser's own message, and echoing it would publish whatever credential it carried.
        Assert.DoesNotContain("ThisIsNotAKeyword", choice.Detail, StringComparison.Ordinal);
    }

    // ================================================================ never a file on hosted

    /// <summary>
    /// A hosted Gateway with nothing to open lands on NOT CONFIGURED. It does NOT open a statistics file -
    /// not here and not anywhere. The self-host arm below is the control: the SAME arguments except for the
    /// deployment produce a SQLite file, so this test can tell "refused a file" from "never offered one".
    /// </summary>
    [Fact]
    public void Resolve_HostedWithNothingConfigured_IsNotConfiguredAndNeverASqliteFile()
    {
        var hosted = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: true, sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.NotConfigured, hosted.Source);
        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, hosted.Reason);
        Assert.Null(hosted.ConnectionString);
        Assert.DoesNotContain(SqlitePath, hosted.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlite", hosted.Target, StringComparison.OrdinalIgnoreCase);

        // CONTROL: identical inputs, self-host. A file IS selected here, which is what makes the refusal
        // above a refusal rather than a path that was never reachable in this fixture.
        var selfHost = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.SqliteFile, selfHost.Source);
        Assert.True(selfHost.IsConfigured);
        Assert.Contains(SqlitePath, selfHost.ConnectionString!, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SelfHostWithNoPostgres_IsTheLocalStatisticsFile()
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.SqliteFile, choice.Source);
        Assert.Equal(StatsStoreUnavailableReason.None, choice.Reason);
        Assert.False(choice.IsPostgres);
        Assert.Contains(SqlitePath, choice.Target, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank Gateway connection is the MAIN database's own fail-loud case and it handles it. Here it means
    /// "no PostgreSQL to derive from", so a self-host Gateway still gets its file rather than being told it
    /// is misconfigured by a variable that is not its.
    /// </summary>
    [Fact]
    public void Resolve_GatewayConnectionBlankOnSelfHost_StillSelectsTheLocalFile()
    {
        var choice = StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: "   ", hosted: false, sqlitePath: SqlitePath);

        Assert.Equal(StatsConnectionSource.SqliteFile, choice.Source);
    }

    // ================================================================ credentials never leave

    [Fact]
    public void Resolve_NeitherTheTargetNorTheDetailCarriesACredential()
    {
        foreach (var choice in new[]
                 {
                     StatsConnectionSelection.Resolve(null, GatewayConnection, true, SqlitePath),
                     StatsConnectionSelection.Resolve(GatewayConnection, null, true, SqlitePath),
                 })
        {
            Assert.DoesNotContain("s3cret", choice.Target, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret", choice.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", choice.Target, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The DERIVED-versus-EXPLICIT distinction is visible, because silent following is only dangerous while
    /// it is silent: somebody who moves the Gateway to a new server needs to see, without asking, that
    /// statistics moved with it.
    /// </summary>
    [Fact]
    public void Resolve_DerivedAndExplicitAreDistinguishableOnTheSurface()
    {
        var derived = StatsConnectionSelection.Resolve(null, GatewayConnection, true, SqlitePath);
        var explicitOverride = StatsConnectionSelection.Resolve(GatewayConnection, null, true, SqlitePath);

        Assert.NotEqual(derived.Source, explicitOverride.Source);
        Assert.NotEqual(derived.Detail, explicitOverride.Detail);

        // The derived one says so in words, and names the variable it followed.
        Assert.Contains("DERIVED", derived.Detail, StringComparison.Ordinal);
        Assert.Contains(
            CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar,
            derived.Detail,
            StringComparison.Ordinal);

        // The derived one is also visibly the statistics application on the wire, which is what separates
        // its connections from the roster's in pg_stat_activity during an incident.
        Assert.Contains(StatsConnectionSelection.StatsApplicationName, derived.Target, StringComparison.Ordinal);
    }
}
