using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// What the statistics surface should say about itself, folded ONCE here and read verbatim by whoever
/// renders it. A client never re-derives this and never guesses: it renders what the Gateway decided.
/// </summary>
/// <param name="IsAvailable">Whether the statistics store is usable at all.</param>
/// <param name="Reason">The named reason it is not, or <see cref="StatsStoreUnavailableReason.None"/>.</param>
/// <param name="ReasonCode">The stable, machine-readable spelling of <paramref name="Reason"/> - what a
/// surface keys off and what an operator greps a log for. Never re-spelled per surface.</param>
/// <param name="Detail">A one-line operator-facing explanation, naming the environment variable involved
/// when one is. Safe to log and to serve: never a connection string and never a credential.</param>
/// <param name="Source">Where the connection came from - and in particular whether it was DERIVED or came
/// from an EXPLICIT override. On the surface deliberately: silent following is only dangerous while it is
/// silent.</param>
/// <param name="Target">A credential-free description of what was selected.</param>
public sealed record StatsStoreAvailability(
    bool IsAvailable,
    StatsStoreUnavailableReason Reason,
    string ReasonCode,
    string Detail,
    StatsConnectionSource Source,
    string Target);

/// <summary>
/// THE FAILURE-DOMAIN BOUNDARY around the Gateway's statistics store. It owns the statistics context's
/// connection, its provider selection and its migration chain - and it CONTAINS every failure in all three,
/// so that none of them can stop the Gateway from starting or from serving.
///
/// WHY THIS EXISTS, AND IT IS NOT A HYPOTHETICAL. On 2026-07-30 the hosted Gateway answered HTTP 500 to
/// every client for thirty-two minutes because a statistics fault propagated out of the roster handler.
/// Removing SQLite from the hosted Gateway must not replace that with a NEW way for statistics to take the
/// whole Gateway down - a statistics database that cannot be reached at startup would do exactly that, one
/// layer earlier and even more completely, because the process would never bind its port at all.
///
/// THIS IS A BOUNDARY, NOT A FALLBACK, AND THE DIFFERENCE IS THE WHOLE POINT. A fallback HIDES a fault by
/// quietly serving something else; a boundary CONTAINS a fault and NAMES it. There is no substitute store
/// here, no alternative path, and no invented data - not a zero, not an empty series, not a stale figure.
/// The statistics surface is simply off, loudly, with the reason named, and everything else keeps working.
/// If anybody ever adds "and if PostgreSQL is down, use a file", that is the fallback this class exists
/// instead of.
///
/// WHAT IS DELIBERATELY NOT TOUCHED. The main <see cref="CcDirector.Gateway.Data.GatewayDatabase"/> keeps its current
/// fatal-on-failure startup behaviour. That is correct and it is load-bearing: it carries the roster, the
/// devices and the tenants, and a Gateway that came up without it would serve wrong answers rather than no
/// answers. Only statistics are contained.
///
/// THE TWO MIGRATION CHAINS NEVER SHARE A TRANSACTION OR A STARTUP GATE. Separate context, separate
/// connection, separate pool, separate schema, separate history table - and separately, this chain does not
/// gate anything. Coupling them would recreate the shared failure domain that separating the contexts
/// existed to avoid, so it is worth saying plainly: this store must never be constructed inside the main
/// database's try block, and its result must never be an input to whether the Gateway starts.
///
/// THE NAMED STATES, AND THE DISTINCTION IS AN ARCHITECT RULING. NOT CONFIGURED, UNREACHABLE and INCOMPLETE
/// SCHEMA are different reasons in the log, on the health surface and in the failure state, because they send
/// the person fixing them to three different places: a setting, a database or network, and the store's own
/// disk. A deploy that simply forgot a variable would otherwise present identically to a database outage, and
/// a half-built schema would present as an outage while the database sits there perfectly healthy - either
/// way the next incident is spent looking somewhere the fault is not.
///
/// Threading: contexts come from a pooled factory (a fresh one per operation, never shared across threads),
/// exactly as the main database hands them out.
/// </summary>
public sealed class GatewayStatsStore : IDisposable
{
    /// <summary>The observer identifier this store reports its own health under.</summary>
    public const string ObserverName = "statistics-store";

    /// <summary>
    /// The longest the statistics store may spend opening and migrating before startup gives up on it and
    /// carries on WITHOUT it.
    ///
    /// A bound rather than a wait, because "non-fatal" is not the same as "harmless". A hosted Gateway has a
    /// platform startup deadline measured in low hundreds of seconds, and that deadline sits in front of the
    /// port bind: a statistics migration that merely BLOCKS long enough would take the site down just as
    /// completely as one that threw, and it would do it without a single exception in the log. So the
    /// containment is on the clock as well as on the exception.
    ///
    /// Twenty seconds is chosen to be comfortably longer than opening and migrating a reachable database,
    /// and far shorter than any deadline it sits in front of.
    /// </summary>
    public static readonly TimeSpan OpenDeadline = TimeSpan.FromSeconds(20);

    private readonly StatsFailureCounters _health = new(ObserverName);
    private readonly ServiceProvider? _provider;
    private bool _disposed;

    /// <summary>What the statistics surface should say about itself. Folded once, here.</summary>
    public StatsStoreAvailability Availability { get; }

    /// <summary>This store's own health, in the shape the statistics failure surface consumes.</summary>
    public IStatsFailureState Health => _health;

    /// <summary>
    /// The pooled statistics context factory, or NULL when the store is unavailable.
    ///
    /// Nullable on purpose, so a consumer cannot use it without having decided what to do when there is no
    /// store. That decision is always the same and it is never a substitute store: record a DROP on the
    /// consumer's own failure state and carry on. The type system asking the question is what stops the
    /// answer from being forgotten on the one path that runs during an incident.
    /// </summary>
    public IDbContextFactory<GatewayStatsDbContext>? Factory { get; }

    /// <summary>Whether the statistics store is usable.</summary>
    public bool IsAvailable => Availability.IsAvailable;

    /// <summary>
    /// Build the statistics store from the environment: the optional
    /// <see cref="StatsConnectionSelection.StatsConnectionEnvVar"/> override, else derivation from
    /// <see cref="CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar"/>, else the local self-host file.
    /// </summary>
    /// <param name="sqlitePath">The self-host statistics file. Defaults to gateway-stats.db under the
    /// storage root - the same file the hand-rolled store has always used. Never opened on a hosted
    /// Gateway.</param>
    /// <param name="hosted">Whether this is a hosted Gateway. Defaults to the running deployment's own
    /// answer. A hosted Gateway NEVER opens a statistics file.</param>
    public static GatewayStatsStore FromEnvironment(string? sqlitePath = null, bool? hosted = null)
    {
        var path = string.IsNullOrWhiteSpace(sqlitePath)
            ? Path.Combine(CcStorage.Root(), "gateway-stats.db")
            : sqlitePath!;

        // Hosted is read from BOTH signals, and either one is enough. GatewayHostedMode.IsHosted is a runtime
        // environment variable that a slot swap or a config restore can drop; IsHostedImage is part of the
        // published artifact and cannot be. Taking either means a hosted container that lost its environment
        // variable still refuses to open a statistics file, which is the direction that matters: the cost of
        // being wrong towards "hosted" is an unavailable statistics surface with a named reason, and the cost
        // of being wrong the other way is a hosted Gateway writing a database onto ephemeral or shared disk.
        var isHosted = hosted ?? (GatewayHostedMode.IsHosted || GatewayHostedMode.IsHostedImage);

        var choice = StatsConnectionSelection.Resolve(
            Environment.GetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar),
            Environment.GetEnvironmentVariable(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar),
            isHosted,
            path);

        return new GatewayStatsStore(choice);
    }

    /// <summary>
    /// Build the statistics store over an already-chosen connection.
    /// </summary>
    /// <param name="choice">The chosen connection, or the named reason there is not one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null. A caller contract
    /// violation - a programming error, not a state a deployment can be in - so it throws.</exception>
    public GatewayStatsStore(StatsConnectionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (!choice.IsConfigured)
        {
            // NOT CONFIGURED. Non-fatal, and LOUD: logged once at startup with the variable named, so an
            // operator reading the log finds the setting rather than starting a network investigation.
            Availability = Unavailable(choice.Reason, choice.Detail, choice.Source, choice.Target);
            _health.RecordFailure(choice.Detail);
            FileLog.Write(
                $"[GatewayStatsStore] Open: statistics are UNAVAILABLE, reason={Availability.ReasonCode}: " +
                $"{choice.Detail} The Gateway starts and serves normally without them.");
            return;
        }

        FileLog.Write(
            $"[GatewayStatsStore] Open: source={choice.Source} target={choice.Target} - " +
            "the statistics migration and its connection failures are NON-FATAL to startup.");

        ServiceProvider? provider = null;
        try
        {
            provider = BuildProvider(choice);
            var factory = provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();

            // Open and migrate WITHIN A BOUND. "Non-fatal" is not the same as "harmless": a hosted Gateway
            // has a platform startup deadline sitting in front of the port bind, so a statistics database
            // that accepts a connection and then never answers would take the site down just as completely
            // as one that threw - and would do it without a single exception in the log. The containment is
            // therefore on the clock as well as on the exception.
            //
            // Task.WhenAny rather than Task.Wait(timeout) so a FAULTED attempt does not surface here as an
            // AggregateException: the real exception is unwrapped by GetResult below and reaches the catch
            // with its own type name, which is what the operator-facing message reports.
            var work = Task.Run(() => OpenAndMigrate(factory, choice));
            var finished = Task.WhenAny(work, Task.Delay(OpenDeadline)).GetAwaiter().GetResult() == work;
            if (!finished)
            {
                // Timed out. The attempt is ABANDONED, not cancelled - a migration in flight must not be
                // torn out from under itself - so the provider is handed to a continuation that releases it
                // once the attempt finally settles. Disposing it here instead would dispose a provider whose
                // pooled context is still in use. The abandoned attempt can never publish anything: only the
                // code below assigns the factory, and this path does not reach it.
                var abandoned = provider;
                provider = null;
                _ = work.ContinueWith(_ => abandoned.Dispose(), TaskScheduler.Default);

                var timedOut =
                    $"The statistics database ({choice.Target}) did not open within " +
                    $"{OpenDeadline.TotalSeconds:0} seconds, so the Gateway stopped waiting for it. The " +
                    "settings name a database, so this is a database or network problem rather than a " +
                    "missing setting. Statistics are unavailable; the Gateway is serving normally and the " +
                    "rest of it is unaffected.";
                Availability = Unavailable(
                    StatsStoreUnavailableReason.Unreachable, timedOut, choice.Source, choice.Target);
                _health.RecordFailure(timedOut);
                FileLog.Write(
                    $"[GatewayStatsStore] Open TIMED OUT (CONTAINED) after {OpenDeadline.TotalSeconds:0} " +
                    $"second(s): source={choice.Source} target={choice.Target}. Statistics are UNAVAILABLE, " +
                    $"reason={Availability.ReasonCode}. The Gateway starts and serves normally without them.");
                return;
            }

            var prepared = work.GetAwaiter().GetResult();
            if (!prepared.IsUsable)
            {
                // The store was reached and interrogated, and it is not one this build can use - an
                // adoption verdict from the self-host path, with its own named reason. Nothing has been
                // changed on disk. Released here rather than held: an unusable store's pooled connections
                // are of no use to anybody.
                provider.Dispose();
                Availability = Unavailable(prepared.Reason, prepared.Detail, choice.Source, choice.Target);
                _health.RecordFailure(prepared.Detail);
                FileLog.Write(
                    $"[GatewayStatsStore] Open: statistics are UNAVAILABLE, reason={Availability.ReasonCode}: " +
                    $"{prepared.Detail}");
                return;
            }

            _provider = provider;
            Factory = factory;
            Availability = new StatsStoreAvailability(
                IsAvailable: true,
                Reason: StatsStoreUnavailableReason.None,
                ReasonCode: "available",
                Detail: choice.Detail,
                Source: choice.Source,
                Target: choice.Target);

            FileLog.Write(
                $"[GatewayStatsStore] Open: ready, source={choice.Source} target={choice.Target}");
        }
        catch (Exception ex)
        {
            // UNREACHABLE. THIS CATCH IS THE BOUNDARY, and it is the reason this class exists: every way the
            // statistics database can fail to open or migrate arrives here, and none of them leaves.
            //
            // The provider is disposed rather than leaked - the constructor is not completing successfully,
            // so nothing else will ever dispose it, and its pooled connections would be held for the life of
            // the process.
            //
            // The exception's MESSAGE is never used. A provider echoes a malformed connection string back in
            // its message, so putting it on a health surface or in a log would publish a credential; the
            // exception TYPE plus the already-redacted target is enough to diagnose, and the full exception
            // is not stringified anywhere.
            provider?.Dispose();

            // OUR FAULT OR THEIRS. A boundary that catches everything cannot, by itself, tell a database
            // that is not there from a bug in this file - and if it guesses "database", every programming
            // error in here gets a plausible infrastructure label and sends the reader somewhere the fault
            // is not. So the failure is CLASSIFIED before it is named.
            var theirs = IsStorageFailure(ex);

            var detail = theirs
                ? $"The statistics database ({choice.Target}) could not be opened or migrated " +
                  $"({ex.GetType().Name}). The settings name a database, so this is a database or network " +
                  "problem rather than a missing setting. Statistics are unavailable; the Gateway is serving " +
                  "normally and the rest of it is unaffected."
                : "Statistics are unavailable because something in DevThrottle's own code failed while " +
                  $"opening the statistics store ({ex.GetType().Name}). This is a fault in DevThrottle, NOT " +
                  "a problem with your database, your network or your settings - checking those will not " +
                  "help, and reporting it to us will. The details are in the Gateway log. The Gateway is " +
                  "serving normally and the rest of it is unaffected.";

            Availability = Unavailable(
                theirs ? StatsStoreUnavailableReason.Unreachable : StatsStoreUnavailableReason.InternalError,
                detail, choice.Source, choice.Target);
            _health.RecordFailure(detail);

            // The STACK is logged for our own faults and not for theirs. For a storage failure the type and
            // the redacted target are the whole diagnosis and a stack is noise; for a fault in our code the
            // stack IS the diagnosis, and without it the operator's report reaches us saying only that
            // something failed. The exception MESSAGE is still never used on either path - a provider echoes
            // a malformed connection string back in its message, and putting that on a surface or in a log
            // would publish a credential.
            FileLog.Write(theirs
                ? $"[GatewayStatsStore] Open FAILED (CONTAINED): source={choice.Source} " +
                  $"target={choice.Target}: {ex.GetType().Name}. Statistics are UNAVAILABLE, " +
                  $"reason={Availability.ReasonCode}. The Gateway starts and serves normally without them."
                : $"[GatewayStatsStore] Open FAILED (CONTAINED) - INTERNAL ERROR, THIS IS OUR BUG: " +
                  $"source={choice.Source} target={choice.Target}: {ex.GetType().FullName}. Statistics are " +
                  $"UNAVAILABLE, reason={Availability.ReasonCode}. The Gateway starts and serves normally " +
                  $"without them. Stack:{Environment.NewLine}{ex.StackTrace}");
        }
    }

    /// <summary>
    /// A fresh statistics context. The caller disposes it (it returns to the pool).
    /// </summary>
    /// <exception cref="InvalidOperationException">The statistics store is unavailable. Explicit, with the
    /// named reason in the message - never a context over a substitute store.</exception>
    public GatewayStatsDbContext CreateContext()
    {
        if (Factory is null)
            throw new InvalidOperationException(
                $"The statistics store is unavailable ({Availability.ReasonCode}): {Availability.Detail}");
        return Factory.CreateDbContext();
    }

    /// <summary>
    /// Record that a statistics observation was deliberately NOT attempted because the store is unavailable.
    /// Callers on the roster path use this instead of attempting a write that cannot succeed, so a refusal
    /// is visible as its own number rather than as silence or as a failure storm.
    /// </summary>
    public void RecordDrop() => _health.RecordDrop();

    /// <summary>Record that this store actually stored something, for the health surface.</summary>
    public void RecordSuccessfulWrite(DateTimeOffset whenUtc) => _health.RecordSuccessfulWrite(whenUtc);

    /// <summary>
    /// Build the pooled context factory for the chosen provider.
    ///
    /// The PostgreSQL path pins BOTH the migrations assembly and this context's OWN history table, in this
    /// context's OWN schema. Neither is a default: without the history table pin the statistics chain would
    /// record itself in the main context's <c>gateway.__EFMigrationsHistory</c>, which is the two chains
    /// sharing a table - the exact coupling separating the contexts existed to avoid.
    /// </summary>
    private static ServiceProvider BuildProvider(StatsConnectionChoice choice)
    {
        var services = new ServiceCollection();

        if (choice.IsPostgres)
        {
            services.AddPooledDbContextFactory<GatewayStatsDbContext>(o =>
                o.UseNpgsql(choice.ConnectionString, npg =>
                {
                    npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
                }));
        }
        else
        {
            // Self-host. The directory is created here because the SQLite provider will not create it, and a
            // first run on a clean machine has no storage root yet.
            var dataSource = new SqliteConnectionStringBuilder(choice.ConnectionString).DataSource;
            var dir = Path.GetDirectoryName(dataSource);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            services.AddPooledDbContextFactory<GatewayStatsDbContext>(o => o.UseSqlite(choice.ConnectionString));
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The actual open: adopt an existing self-host store if there is one to adopt, then apply the chain.
    /// </summary>
    private static StatsStoreAdoptionResult OpenAndMigrate(
        IDbContextFactory<GatewayStatsDbContext> factory, StatsConnectionChoice choice)
    {
        using var context = factory.CreateDbContext();

        // Self-host only. Every self-host machine that has ever opened the statistics page has a file written
        // before this chain existed - sixteen tables and no migration history - and adoption is what makes
        // the chain understand it. A PostgreSQL statistics store has never existed without a history table,
        // so there is nothing there to adopt and asking would be a caller contract violation.
        if (!choice.IsPostgres)
        {
            var adoption = GatewayStatsSqliteAdoption.Adopt(context);
            if (!adoption.IsUsable)
                return adoption;
        }

        // Ask BEFORE applying. GetPendingMigrations reads the history table and compares; Migrate takes an
        // exclusive database-wide advisory lock before it looks at whether there is anything to do, and holds
        // it for the whole call. On PostgreSQL that lock is on the database the Gateway's OWN chain also
        // migrates, so calling Migrate unconditionally would make the statistics chain WAIT on the main one
        // during every deploy - two chains that share no table, no transaction and no gate, queueing behind
        // one lock anyway. On a code-only deploy the answer here is "none" and the lock is never taken.
        //
        // This is not a weakened contract: when there ARE migrations they are applied exactly as before, and
        // a failure is contained exactly as before. It only removes a lock acquisition with nothing to do.
        var pending = context.Database.GetPendingMigrations().ToList();

        // A HALF-BUILT SCHEMA, DIAGNOSED BEFORE IT IS WALKED INTO. The history records nothing as applied
        // while tables this store owns are already there, so the chain is about to create a table that
        // exists and fail. That failure would be CONTAINED either way - the catch around this work is the
        // boundary and it holds - but it would be contained as UNREACHABLE, whose sentence sends the
        // operator to the database and the network while both are perfectly healthy and the fault is on the
        // store's own disk. So the state is checked and NAMED rather than caught and mis-named.
        //
        // This is a diagnosis and not a second boundary: it changes which reason is reported and nothing
        // else. If it ever fails to spot the state, the chain throws and the catch still contains it.
        //
        // Left exactly as found. Repair means deciding what to do about tables holding somebody's numbers,
        // and a startup path that quietly reshapes a store is how numbers disappear without anybody knowing
        // which build did it.
        if (pending.Count > 0 && !context.Database.GetAppliedMigrations().Any())
        {
            var alreadyThere = ExistingModelTables(context, choice);
            if (alreadyThere.Count > 0)
            {
                var detail =
                    $"The statistics store ({choice.Target}) has a HALF-BUILT SCHEMA: its migration history " +
                    $"records nothing as applied, yet {alreadyThere.Count} table(s) it owns already exist " +
                    $"({string.Join(", ", alreadyThere)}). That is what a process stopped part-way through " +
                    "its first migration leaves behind. The database is reachable and the settings are " +
                    "correct, so this is NOT a network or connection problem - it is the store on disk, and " +
                    "it has NOT been changed in any way. Statistics are unavailable; the Gateway is serving " +
                    "normally and the rest of it is unaffected.";
                FileLog.Write($"[GatewayStatsStore] Migrate: REFUSED, half-built schema: {detail}");
                return new StatsStoreAdoptionResult(
                    StatsStoreAdoptionOutcome.NotAdoptable,
                    StatsStoreUnavailableReason.StoreSchemaIncomplete,
                    detail);
            }
        }

        if (pending.Count == 0)
        {
            FileLog.Write("[GatewayStatsStore] Migrate: no pending statistics migrations - skipping " +
                          "Migrate() and its database-wide lock");
        }
        else
        {
            FileLog.Write($"[GatewayStatsStore] Migrate: {pending.Count} pending statistics migration(s), " +
                          $"applying: {string.Join(", ", pending)}");
            context.Database.Migrate();
            FileLog.Write($"[GatewayStatsStore] Migrate: applied {pending.Count} statistics migration(s)");
        }

        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.AlreadyTracked, StatsStoreUnavailableReason.None,
            "The statistics store is open and its schema is up to date.");
    }

    /// <summary>
    /// Whether a failure caught by the boundary is a STORAGE failure - the operator's database, network or
    /// file - as opposed to a fault in DevThrottle's own code.
    ///
    /// NOT A TYPE-NAME WHITELIST, because a list of spellings rots: it silently reclassifies the day a
    /// provider renames an exception or a new provider is added, and nothing fails when it does. This asks
    /// the .NET type SYSTEM instead, using the base types that are the framework's OWN contract for
    /// "a data provider or the transport underneath it failed":
    ///
    ///  - <see cref="System.Data.Common.DbException"/> is the base every ADO.NET provider derives its own
    ///    exceptions from - <c>NpgsqlException</c> and <c>SqliteException</c> both do - so a new provider is
    ///    classified correctly without this method being edited.
    ///  - <see cref="System.Net.Sockets.SocketException"/> and <see cref="IOException"/> are the transport
    ///    and the local file underneath it.
    ///  - <see cref="TimeoutException"/> is a wait that expired against something outside this process.
    ///
    /// The whole INNER CHAIN is walked, because Entity Framework routinely wraps a provider exception in one
    /// of its own; a genuine outage that arrived wrapped must not be called our bug.
    ///
    /// WHAT THIS RULE CANNOT CLASSIFY, named rather than silently bucketed, because there are real cases in
    /// the middle and pretending otherwise is how a classifier earns unwarranted trust:
    ///
    ///  1. OUR bug thrown while a provider exception is already in flight - our code failing inside a catch,
    ///     chaining theirs - reads as THEIRS, because their exception is in the chain.
    ///  2. <see cref="InvalidOperationException"/> from Entity Framework with no inner provider exception
    ///     reads as OURS. That is right far more often than not - it is the shape of a model or mapping
    ///     mistake - but a genuinely malformed connection string can surface the same way.
    ///  3. <see cref="IOException"/> reads as THEIRS, and a bug in our own path handling surfaces as one too.
    ///
    /// The bias is deliberate and it is toward NOT crying wolf about the operator's infrastructure: every
    /// case above that lands wrong lands on the side of a truthful-but-vaguer message rather than sending
    /// somebody to audit a healthy network.
    /// </summary>
    public static bool IsStorageFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Data.Common.DbException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Which of THIS MODEL'S tables already exist in the store, read from the provider's own catalog.
    ///
    /// The expected set comes from the MODEL rather than from a list written out here, so it cannot drift
    /// from the schema the baseline migration actually creates - a hand-written list would go stale the first
    /// time a table is added and would then report a half-built store as healthy.
    ///
    /// The catalog query is provider-specific because there is no portable one, and the PostgreSQL side is
    /// scoped to this context's OWN schema deliberately: the statistics schema shares a database with the
    /// Gateway's own, so a query that asked "does this database have tables" would answer yes on every
    /// healthy hosted Gateway and report a half-built statistics store that does not exist.
    /// </summary>
    private static List<string> ExistingModelTables(GatewayStatsDbContext context, StatsConnectionChoice choice)
    {
        var expected = context.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            context.Database.OpenConnection();

        using var command = connection.CreateCommand();
        if (choice.IsPostgres)
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema";
            var schema = command.CreateParameter();
            schema.ParameterName = "@schema";
            schema.Value = GatewayStatsDbContext.PostgresSchema;
            command.Parameters.Add(schema);
        }
        else
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        }

        var present = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (expected.Contains(name))
                present.Add(name);
        }

        present.Sort(StringComparer.Ordinal);
        return present;
    }

    private static StatsStoreAvailability Unavailable(
        StatsStoreUnavailableReason reason, string detail, StatsConnectionSource source, string target) =>
        new(IsAvailable: false, reason, CodeFor(reason), detail, source, target);

    /// <summary>
    /// The stable, machine-readable spelling of a reason. Written out rather than derived from the enum name
    /// so that renaming a member in C# cannot silently change a string an operator greps for or a surface
    /// keys off.
    ///
    /// A MEMBER WITH NO CODE HERE IS A PROGRAMMING ERROR AND THROWS - and this comment used to claim it
    /// "fails to compile", which was simply false and was worth more than the mistake it hid. A C# switch
    /// expression over an enum does NOT fail to compile on a missing member; it throws at RUN TIME. Worse,
    /// on the one path that matters the throw lands inside the boundary catch above, which reports it as
    /// UNREACHABLE - so a missing code does not crash loudly, it silently mis-names somebody's disk problem
    /// as a network problem. That is precisely the failure the named reasons exist to prevent, arriving
    /// through the mechanism meant to guarantee them.
    ///
    /// It happened: worker 2 added two adoption reasons on its own branch and this map did not know them.
    /// So the guarantee is now MECHANICAL rather than a comment asking to be believed -
    /// <c>StatsStoreReasonCodeTests</c> walks every member of the enum and fails when one has no code or
    /// shares another's. A rule that depends on somebody remembering is not a rule.
    /// </summary>
    public static string CodeFor(StatsStoreUnavailableReason reason) => reason switch
    {
        StatsStoreUnavailableReason.None => "available",
        StatsStoreUnavailableReason.NotConfigured => "not_configured",
        StatsStoreUnavailableReason.Unreachable => "unreachable",
        StatsStoreUnavailableReason.StoreSchemaIncomplete => "store_schema_incomplete",
        StatsStoreUnavailableReason.IncompatibleSchemaVersion => "incompatible_schema_version",
        StatsStoreUnavailableReason.NotAStatisticsStore => "not_a_statistics_store",
        StatsStoreUnavailableReason.StoreUnreadable => "store_unreadable",
        StatsStoreUnavailableReason.StoreSchemaIncomplete => "store_schema_incomplete",
        StatsStoreUnavailableReason.MigrationHistoryIncomplete => "migration_history_incomplete",
        StatsStoreUnavailableReason.InternalError => "internal_error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason,
            "A statistics unavailability reason with no stable code. Add one here - a surface cannot key " +
            "off a reason it has no spelling for."),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider?.Dispose();
        // Release the underlying SQLite connections so a test can delete the file. SQLite-only: the
        // PostgreSQL path has no local file and its pooling is the provider's to manage.
        if (Availability.Source == StatsConnectionSource.SqliteFile)
            SqliteConnection.ClearAllPools();
        FileLog.Write($"[GatewayStatsStore] Dispose: closed {Availability.Target}");
    }
}
