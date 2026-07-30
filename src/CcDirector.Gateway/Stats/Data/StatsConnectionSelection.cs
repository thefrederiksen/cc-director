using Npgsql;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// Where the statistics store's connection came from. This is on the health surface deliberately: silent
/// following is only dangerous while it is silent. Somebody who moves the Gateway to a new database server
/// needs to see, without asking anybody, that statistics moved with it.
/// </summary>
public enum StatsConnectionSource
{
    /// <summary>The local SQLite statistics file - the self-host path, unchanged. Only ever chosen when the
    /// Gateway is NOT hosted; a hosted Gateway never opens a statistics file, under any circumstance.</summary>
    SqliteFile,

    /// <summary><see cref="StatsConnectionSelection.StatsConnectionEnvVar"/> was set, so it won outright.
    /// The operator named this database explicitly and nothing was derived.</summary>
    ExplicitOverride,

    /// <summary>No override was set and the Gateway is on PostgreSQL, so the statistics connection was
    /// DERIVED from <see cref="CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar"/> - same server, same
    /// credentials, SAME DATABASE, with its own application name and its own pool.</summary>
    DerivedFromGatewayDatabase,

    /// <summary>No statistics store could be selected at all. A named configuration state, NOT a failure to
    /// reach a database - see <see cref="StatsStoreUnavailableReason.NotConfigured"/>.</summary>
    NotConfigured,
}

/// <summary>
/// The chosen statistics connection, or the named reason there is not one.
/// </summary>
/// <param name="Source">Where the connection came from, for the health surface.</param>
/// <param name="ConnectionString">The connection string to open, or null when
/// <paramref name="Source"/> is <see cref="StatsConnectionSource.NotConfigured"/>. NEVER logged and never
/// put on a surface - it carries credentials on the PostgreSQL paths.</param>
/// <param name="Target">A credential-free description of what was selected, safe to log and to serve.</param>
/// <param name="Reason">The named reason when there is no connection; otherwise
/// <see cref="StatsStoreUnavailableReason.None"/>.</param>
/// <param name="Detail">A one-line operator-facing explanation, safe to log and to serve. Names the
/// environment variable involved, because a deploy that forgot one presents identically to an outage
/// otherwise.</param>
public sealed record StatsConnectionChoice(
    StatsConnectionSource Source,
    string? ConnectionString,
    string Target,
    StatsStoreUnavailableReason Reason,
    string Detail)
{
    /// <summary>Whether a store can be opened at all. False means the statistics surface is unavailable with
    /// <see cref="Reason"/> and the Gateway carries on without it.</summary>
    public bool IsConfigured => Source != StatsConnectionSource.NotConfigured;

    /// <summary>Whether the chosen store is PostgreSQL (either source of one).</summary>
    public bool IsPostgres =>
        Source is StatsConnectionSource.ExplicitOverride or StatsConnectionSource.DerivedFromGatewayDatabase;
}

/// <summary>
/// Chooses the statistics store's connection: the optional override, else derivation from the Gateway's own
/// PostgreSQL connection, else the local SQLite file - and, when none of those applies, the NAMED reason
/// there is no store.
///
/// WHY THE STATISTICS CONNECTION IS DERIVED RATHER THAN BEING A SECOND SECRET. Npgsql keys its connection
/// POOLS by the connection string. Handing both contexts the identical string would silently collapse them
/// into ONE pool and delete the pool separation this whole design rests on - while looking perfectly
/// separated in the code, because there would still be two contexts, two schemas and two migration chains.
/// So the derived string differs from the Gateway's in its application name (<c>gateway-stats</c>, which
/// also names the statistics connections in <c>pg_stat_activity</c> during an incident) and in its own
/// explicit pool sizing. Two different strings, therefore two different pools, therefore a statistics store
/// that cannot exhaust the pool the roster is served from.
///
/// WHAT IS DERIVED AND WHAT IS NOT. The server, the port, the credentials and every transport setting come
/// across untouched, because "statistics live beside the Gateway's own database" is the entire contract.
/// THE DATABASE NAME IS NEVER DERIVED - it is carried, unaltered, by construction: the builder starts FROM
/// the Gateway's own string and only ever writes the pooling keys. A derivation that could quietly point at
/// a different database is a different feature, and one whose failure mode is writing a tenant's numbers
/// somewhere nobody is looking.
///
/// THE THREE STATES ARE NAMED SEPARATELY AND THAT IS NOT OPTIONAL. NOT CONFIGURED (a self-host
/// misconfiguration, or an override that is SET BUT BLANK) and UNREACHABLE (configured, but the database
/// cannot be reached) are different reasons in the log, on the health surface and in the failure state. A
/// deploy that simply forgot a variable would otherwise present identically to a database outage, and the
/// next incident would be spent hunting a network fault that is really a missing setting.
/// </summary>
public static class StatsConnectionSelection
{
    /// <summary>The OPTIONAL override. Set to a real PostgreSQL connection string and it wins outright, on
    /// either deployment. Set to a BLANK value and that is an operator error, reported as
    /// <see cref="StatsStoreUnavailableReason.NotConfigured"/> - never quietly treated as unset, which would
    /// be exactly the hidden fallback the no-fallback rule forbids.</summary>
    public const string StatsConnectionEnvVar = "CC_GATEWAY_STATS_DB_CONNECTION";

    /// <summary>The application name stamped on every DERIVED statistics connection. It is what makes the
    /// derived string DIFFERENT from the Gateway's own, and therefore what makes the separate Npgsql pool
    /// real rather than nominal. It also names these connections in <c>pg_stat_activity</c>, so an incident
    /// can tell statistics traffic from roster traffic without guessing.</summary>
    public const string StatsApplicationName = "gateway-stats";

    /// <summary>The statistics store's OWN maximum pool size, set explicitly on a derived connection rather
    /// than inherited. Deliberately small: the statistics store does bounded, low-frequency work, and a
    /// bound it cannot exceed is what stops a statistics stall from consuming connections the roster needs.
    /// </summary>
    public const int StatsMaxPoolSize = 8;

    /// <summary>The statistics store's OWN minimum pool size. Zero: an idle statistics store should hold no
    /// connection open at all. Set explicitly rather than inherited so an unusually large minimum on the
    /// Gateway's own string can never exceed <see cref="StatsMaxPoolSize"/> here.</summary>
    public const int StatsMinPoolSize = 0;

    /// <summary>
    /// Choose the statistics connection from the two environment variables and the deployment.
    /// </summary>
    /// <param name="statsOverride">The raw value of <see cref="StatsConnectionEnvVar"/>: null when unset,
    /// and DISTINCT from an empty string, which is an operator error.</param>
    /// <param name="gatewayConnection">The raw value of
    /// <see cref="CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar"/>. A blank value is the main database's own
    /// fail-loud case, which it handles; here it is treated as "no PostgreSQL to derive from".</param>
    /// <param name="hosted">Whether this is a hosted Gateway. A hosted Gateway NEVER opens a SQLite
    /// statistics file, so when nothing else selects a store the answer is
    /// <see cref="StatsStoreUnavailableReason.NotConfigured"/>, never a file.</param>
    /// <param name="sqlitePath">The self-host statistics file. Only used on the SQLite path.</param>
    public static StatsConnectionChoice Resolve(
        string? statsOverride, string? gatewayConnection, bool hosted, string sqlitePath)
    {
        // 1. The override, SET BUT BLANK. A real operator error - somebody meant to point statistics at a
        //    database and left the value empty - and it is reported as such rather than being read as unset.
        //    Reading it as unset would silently pick a different store than the one the operator was trying
        //    to configure, which is the hidden fallback the no-fallback rule exists to prevent.
        if (statsOverride is not null && string.IsNullOrWhiteSpace(statsOverride))
            return NotConfigured(
                $"{StatsConnectionEnvVar} is set but blank. Set a real PostgreSQL connection string, or " +
                "unset it entirely so the statistics connection is derived from " +
                $"{CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar} (hosted) or the local statistics file " +
                "(self-host) is used. Statistics are unavailable; the rest of the Gateway is unaffected.");

        // 2. The override, set to something. It wins outright: the operator named this database explicitly,
        //    so nothing is derived and nothing is second-guessed.
        if (statsOverride is not null)
            return new StatsConnectionChoice(
                StatsConnectionSource.ExplicitOverride,
                statsOverride,
                Describe(statsOverride),
                StatsStoreUnavailableReason.None,
                $"The statistics store is the PostgreSQL database named explicitly by {StatsConnectionEnvVar}.");

        // 3. No override, and the Gateway itself is on PostgreSQL: derive. Same server, same credentials,
        //    same database; its own application name and its own pool.
        if (!string.IsNullOrWhiteSpace(gatewayConnection))
            return Derive(gatewayConnection!);

        // 4. Hosted, with nothing to derive from and no override. NOT a SQLite file - not here and not
        //    anywhere. A hosted Gateway writing a statistics file would be writing it into a container's
        //    ephemeral disk or onto a share two containers can corrupt, which is the incident this mission
        //    exists to end. It is a named configuration state instead, and the Gateway still serves.
        if (hosted)
            return NotConfigured(
                $"This is a hosted Gateway and neither {StatsConnectionEnvVar} nor " +
                $"{CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar} names a PostgreSQL database, so there is " +
                "no statistics store to open. A hosted Gateway NEVER opens a local statistics file. Set " +
                $"{StatsConnectionEnvVar}, or set {CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar} and the " +
                "statistics connection is derived from it. Statistics are unavailable; the rest of the " +
                "Gateway is unaffected.");

        // 5. Self-host: the local SQLite statistics file, exactly as before. This mission is no SQLite on the
        //    HOSTED Gateway; self-host keeping its file is correct and is not a compromise.
        return new StatsConnectionChoice(
            StatsConnectionSource.SqliteFile,
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString(),
            $"sqlite path={sqlitePath}",
            StatsStoreUnavailableReason.None,
            $"The statistics store is the local file at '{sqlitePath}'.");
    }

    /// <summary>
    /// Derive the statistics connection from the Gateway's own. The builder is constructed FROM the
    /// Gateway's string and only the pooling keys are written, so the server, the credentials and - the one
    /// that matters most - the DATABASE NAME are carried across unaltered by construction rather than by a
    /// rule somebody has to keep following.
    /// </summary>
    private static StatsConnectionChoice Derive(string gatewayConnection)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(gatewayConnection);
        }
        catch (Exception ex)
        {
            // The Gateway's own connection string does not parse. That is a CONFIGURATION error, not an
            // unreachable database, and the two are named separately on purpose - this one is fixed by
            // editing a setting, never by investigating the network. The main GatewayDatabase fails loudly
            // on the same string, which is correct for the database that carries the roster; statistics
            // report it and carry on. The exception message can echo the raw string back, so only its TYPE
            // name is used here - never its message and never the string.
            return NotConfigured(
                $"{CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar} could not be parsed as a PostgreSQL " +
                $"connection string ({ex.GetType().Name}), so no statistics connection could be derived " +
                $"from it. Set {StatsConnectionEnvVar} explicitly, or fix " +
                $"{CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar}. Statistics are unavailable; the rest of " +
                "the Gateway is unaffected.");
        }

        builder.ApplicationName = StatsApplicationName;
        builder.MinPoolSize = StatsMinPoolSize;
        builder.MaxPoolSize = StatsMaxPoolSize;

        var derived = builder.ToString();
        return new StatsConnectionChoice(
            StatsConnectionSource.DerivedFromGatewayDatabase,
            derived,
            Describe(derived),
            StatsStoreUnavailableReason.None,
            "The statistics connection was DERIVED from " +
            $"{CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar}: the same server, the same credentials and " +
            $"the same database, with application name '{StatsApplicationName}' and its own connection " +
            $"pool (maximum {StatsMaxPoolSize}). Set {StatsConnectionEnvVar} to override it.");
    }

    private static StatsConnectionChoice NotConfigured(string detail) =>
        new(StatsConnectionSource.NotConfigured, null, "none", StatsStoreUnavailableReason.NotConfigured, detail);

    /// <summary>
    /// A credential-free description of a PostgreSQL target, for logging and for the health surface: host,
    /// database and application name only.
    ///
    /// The redaction itself is <see cref="CcDirector.Gateway.Data.GatewayDatabase.RedactConnectionTarget"/> rather than a second
    /// implementation here. There is exactly one place in this process that knows how to turn a PostgreSQL
    /// connection string into something safe to print, and a second one would be a second thing to keep
    /// correct - the failure mode being a password in a log nobody redacts twice.
    /// </summary>
    private static string Describe(string connectionString)
    {
        var target = CcDirector.Gateway.Data.GatewayDatabase.RedactConnectionTarget(connectionString);
        try
        {
            var application = new NpgsqlConnectionStringBuilder(connectionString).ApplicationName;
            return string.IsNullOrEmpty(application) ? target : $"{target} application={application}";
        }
        catch
        {
            // The string does not parse. RedactConnectionTarget has already degraded to its fixed literal
            // for exactly the same reason, so there is nothing further to say and nothing unsafe to print.
            return target;
        }
    }
}
