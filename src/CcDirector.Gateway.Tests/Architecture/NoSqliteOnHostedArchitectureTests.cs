using CcDirector.Gateway.Data;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// DT-SQL: the no-SQLite-on-hosted guard. The hosted Gateway must not keep a SQLite database.
///
/// WHY. On 2026-07-30 the hosted Gateway answered HTTP 500 to every client for 32 minutes: it keeps a SQLite
/// database on an Azure Files network share, a deploy ran two containers against that share, and the file was
/// corrupted. The remediation removes that database. THIS is what stops it coming back - the next hand that
/// adds an innocent-looking SQLite-backed store to the Gateway is stopped by a red test, not by somebody
/// remembering the incident.
///
/// WHY THE RULE IS STATIC RATHER THAN A STARTUP OR RUNTIME ASSERTION, which is the whole design decision here.
/// A runtime assertion - at startup, or inside a composed test host - only fires on a path that actually RUNS.
/// The thing being defended against is the store nobody has written yet, whose code will sit behind a lazy
/// initializer, a background sweep, or an endpoint no test calls. Such a guard passes GREEN for the exact
/// reason it should be red: nothing exercised the code that would open the connection. There is also no
/// supported process-wide interception point for <c>new SqliteConnection(...).Open()</c> - Microsoft.Data.Sqlite
/// exposes no global hook - so a runtime guard can only ever bind the sites that remember to CALL it, which is
/// precisely the "only checks the call site we already know about" weakness that makes a guard worth little.
/// A metadata scan has neither problem: it reads what the compiled assembly CAN do, so it does not care
/// whether any path ran, and it sees every site whether or not that site opted in.
///
/// AND THE POLARITY IS AN ALLOWLIST, NOT A DENYLIST. A denylist catches the call sites already known. An
/// allowlist catches every OTHER one - so a type nobody has written yet is caught by construction, because it
/// is not on the list. Adding a type to <see cref="SqliteTouchingTypes"/> is a deliberate, review-visible act
/// carrying a written reason, exactly as <c>TenantGateArchitectureTests.GlobalUnscopedTables</c> works for
/// un-scoped tables.
///
/// The rules:
///  - DT-SQL-1: no type in the hosted Gateway assemblies touches SQLite unless it is named on
///    <see cref="SqliteTouchingTypes"/>.
///  - DT-SQL-2: every name on <see cref="SqliteTouchingTypes"/> still exists AND still touches SQLite, so the
///    allowlist cannot rot into ghosts that permit things nobody checked. It tightens itself: when the
///    remediation takes SQLite out of the statistics store, that entry goes RED and must be DELETED.
///  - DT-SQL-3: <see cref="HostedSqliteGuard"/>'s notion of "hosted" agrees with the provider
///    <see cref="GatewayDatabase"/> actually selects, across all three states of the variable.
///  - DT-SQL-4: <see cref="HostedSqliteGuard.EnsureNotHosted"/> refuses on hosted and names the path, and
///    does nothing on self-host.
///
/// SELF-HOST KEEPS SQLITE AND THAT IS CORRECT. The rule is no SQLite on the HOSTED Gateway. Nothing here
/// forbids the SQLite code from existing or from running on a desktop install.
///
/// This class joins the <c>GatewayHostedMode</c> collection under that collection's rule 1: DT-SQL-3 and
/// DT-SQL-4 SET a process-wide variable that decides whether a Gateway is hosted. The variable is
/// <c>CC_GATEWAY_DB_CONNECTION</c> rather than <c>CC_GATEWAY_HOSTED</c>, but the hazard is identical and
/// worse - anything constructing a GatewayDatabase in parallel would silently be handed the other test's
/// provider. The collection disables parallelization, which is what makes that impossible.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class NoSqliteOnHostedArchitectureTests
{
    /// <summary>
    /// The assemblies that ARE the hosted Gateway service: the Gateway itself and the container entry point
    /// that wraps it. Both are scanned, and a missing one is a RED test rather than a smaller silent scope -
    /// see <see cref="LoadHostedGatewayModules"/>.
    /// </summary>
    private static readonly string[] HostedGatewayAssemblyFiles =
    {
        "CcDirector.Gateway.dll",
        "CcDirector.Gateway.Host.dll",
    };

    /// <summary>
    /// The types permitted to touch SQLite inside the hosted Gateway assemblies, each with the reason it
    /// cannot hand a hosted Gateway a SQLite file. This list is the guard. A type that is not here and touches
    /// SQLite fails DT-SQL-1 by name.
    ///
    /// ADDING A NAME HERE IS THE ONLY WAY PAST THE GUARD, AND IT IS MEANT TO BE UNCOMFORTABLE. Before adding
    /// one, the question to answer is not "does my store work" but "what makes it unreachable on hosted". The
    /// three answers that have ever been good enough are below: the site is branch-gated on the very variable
    /// that means hosted; the site never runs in the Gateway process at all; or the site calls
    /// <see cref="HostedSqliteGuard.EnsureNotHosted"/> before it opens anything. "It is only a small file" is
    /// not one of them - the 32-minute outage was a small file.
    /// </summary>
    private static readonly Dictionary<string, string> SqliteTouchingTypes = new(StringComparer.Ordinal)
    {
        [nameof(GatewayDatabase)] =
            "BRANCH-GATED on the hosted marker itself: every SQLite statement in it sits behind the same " +
            "CC_GATEWAY_DB_CONNECTION test that means hosted, so a hosted Gateway takes the PostgreSQL branch " +
            "and cannot reach the file. This is the shape every other entry is measured against.",

        [nameof(GatewayDbContextDesignTimeFactory)] =
            "DESIGN-TIME ONLY: constructed by the 'dotnet ef' tooling to scaffold migrations, never by the " +
            "Gateway process. It serves no request and holds no data.",

        ["GatewayStatsDatabase"] =
            "THE SUBJECT OF THE REMEDIATION, not an exemption. This is the store whose file on the Azure Files " +
            "share caused the 2026-07-30 outage; it is UNGATED today and does open SQLite on hosted. It is " +
            "named here so this guard passes against the CURRENT tree while the port is in flight. DELETE this " +
            "entry when the port lands - DT-SQL-2 will go red and tell you to.",

        ["GatewayInputStatsAggregator"] =
            "Reads and writes through the connection GatewayStatsDatabase already opened; it opens none of its " +
            "own. It is gated exactly as well as that store is, and it goes away with it. DELETE this entry " +
            "with the one above.",
    };

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-1: the allowlist is total.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-1: every type in the hosted Gateway assemblies that touches SQLite is named on
    /// <see cref="SqliteTouchingTypes"/>. "Touches" is read off the compiled metadata (Mono.Cecil), not off
    /// the source text, so it cannot be walked around by an alias, a <c>using static</c>, a differently-named
    /// helper, a partial class, a new file, or generated code: whatever the source looked like, the IL names
    /// the type it calls.
    ///
    /// Revert-proof: add a type anywhere in the Gateway that opens, builds or holds a SQLite connection and
    /// this test goes red naming that type and the SQLite member it reached for. Watched tripping - see
    /// docs/step2-nosqlite-guard-proof.md.
    /// </summary>
    [Fact]
    public void DT_SQL_1_no_unlisted_type_in_the_hosted_gateway_touches_sqlite()
    {
        var offenders = new List<string>();

        foreach (var (file, module) in LoadHostedGatewayModules())
        {
            using (module)
            {
                foreach (var type in AllTypes(module))
                {
                    var touch = FirstSqliteTouch(type);
                    if (touch is null) continue;

                    var owner = TopLevel(type).Name;
                    if (!SqliteTouchingTypes.ContainsKey(owner))
                        offenders.Add($"{file}: {type.FullName} touches {touch}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DT-SQL-1: the hosted Gateway must keep NO SQLite database - its file lives on a network share, " +
            "where a deploy running two containers corrupts it (the 2026-07-30 outage, 32 minutes of HTTP 500 " +
            "to every client). These types touch SQLite and are not named on SqliteTouchingTypes:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders.Distinct()) +
            Environment.NewLine +
            "Put the data in PostgreSQL (the hosted Gateway already has one), or - if this really cannot reach " +
            "a hosted Gateway - add the type to SqliteTouchingTypes with the reason it cannot, and call " +
            "HostedSqliteGuard.EnsureNotHosted before opening anything. Self-host keeps SQLite and is unaffected.");
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-2: the allowlist cannot rot.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-2: every name on <see cref="SqliteTouchingTypes"/> still exists in the scanned assemblies AND
    /// still touches SQLite. A known-failure allowance goes stale: an entry whose type was renamed, deleted,
    /// or cleaned of SQLite is an allowance nobody is checking, and the next type to take that name inherits
    /// a permission it was never granted.
    ///
    /// This is also what makes the guard TIGHTEN as the remediation lands: the moment the statistics store
    /// stops opening SQLite, its entry here goes red and must be deleted, and the allowlist shrinks to the two
    /// entries that are structurally safe.
    /// </summary>
    [Fact]
    public void DT_SQL_2_every_allowlisted_type_still_exists_and_still_touches_sqlite()
    {
        var stillTouching = new HashSet<string>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, module) in LoadHostedGatewayModules())
        {
            using (module)
            {
                foreach (var type in AllTypes(module))
                {
                    var owner = TopLevel(type).Name;
                    present.Add(owner);
                    if (FirstSqliteTouch(type) is not null) stillTouching.Add(owner);
                }
            }
        }

        var gone = SqliteTouchingTypes.Keys.Where(n => !present.Contains(n)).ToList();
        var cleaned = SqliteTouchingTypes.Keys.Where(n => present.Contains(n) && !stillTouching.Contains(n)).ToList();

        Assert.True(gone.Count == 0,
            "DT-SQL-2: these names are on SqliteTouchingTypes but no such type exists in the hosted Gateway " +
            "assemblies any more. A stale allowance is a permission nobody is checking, and the next type to " +
            "take the name inherits it. Delete them: " + string.Join(", ", gone));

        Assert.True(cleaned.Count == 0,
            "DT-SQL-2: these types are allowlisted for SQLite but no longer touch it - which is GOOD NEWS. " +
            "Delete their entries from SqliteTouchingTypes so the allowlist shrinks to what is really still " +
            "there, and the guard tightens around it: " + string.Join(", ", cleaned));
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-3 / DT-SQL-4: the runtime refusal.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-3: <see cref="HostedSqliteGuard.IsHosted"/> agrees with the provider <see cref="GatewayDatabase"/>
    /// ACTUALLY selects, in all three states of the variable. Asserting the two read the same variable NAME
    /// would prove nothing - they share one compile-time constant, so that comparison cannot fail and a check
    /// that cannot fail is not a check. This drives the real selection instead and observes which provider came
    /// out: SQLite opens a file, PostgreSQL fails to reach a dead endpoint, blank is rejected as a
    /// misconfiguration. If the guard's predicate is ever loosened away from the Gateway's, this goes red.
    /// </summary>
    [Fact]
    public void DT_SQL_3_the_guards_notion_of_hosted_matches_the_provider_the_gateway_selects()
    {
        var original = Environment.GetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar);
        var dir = Path.Combine(Path.GetTempPath(), "dt-sql-3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // UNSET: self-host. The guard says not hosted, and the Gateway really does open a SQLite file.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, null);
            Assert.False(HostedSqliteGuard.IsHosted);
            var dbPath = Path.Combine(dir, "gateway.db");
            using (var db = new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), dbPath))
                Assert.Equal(dbPath, db.Path);
            Assert.True(File.Exists(dbPath), "the unset case must really have opened a SQLite FILE, not merely reported one");

            // SET: hosted. The guard says hosted, and the Gateway selects PostgreSQL - proven by it failing to
            // reach a dead endpoint rather than quietly opening a file. Port 1 refuses immediately.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar,
                "Host=127.0.0.1;Port=1;Database=dt_sql_3;Username=u;Password=p;Timeout=1;Command Timeout=1");
            Assert.True(HostedSqliteGuard.IsHosted);
            var pgPath = Path.Combine(dir, "must-not-be-created.db");
            var pgFailure = Assert.Throws<InvalidOperationException>(
                () => new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), pgPath));
            Assert.Contains("PostgreSQL", pgFailure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(pgPath),
                "the hosted case must not have created a SQLite file even though a path was supplied");

            // SET BUT BLANK: hosted, failing CLOSED. A misconfiguration must never be what hands a hosted
            // Gateway a SQLite file, so the guard reads blank as hosted and the Gateway refuses outright.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, "   ");
            Assert.True(HostedSqliteGuard.IsHosted);
            var blankFailure = Assert.Throws<InvalidOperationException>(
                () => new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), pgPath));
            Assert.Contains("blank", blankFailure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(pgPath),
                "the blank case must not have fallen back to a SQLite file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, original);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// DT-SQL-4: the runtime refusal does what it says - throws on hosted with the offending path NAMED in the
    /// message (so the next incident reads the answer instead of hunting for it), and does nothing at all on
    /// self-host, which keeps SQLite and must be untouched.
    /// </summary>
    [Fact]
    public void DT_SQL_4_the_runtime_guard_refuses_on_hosted_and_names_the_path()
    {
        var original = Environment.GetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar,
                "Host=example.invalid;Database=d;Username=u;Password=p");

            var ex = Assert.Throws<InvalidOperationException>(
                () => HostedSqliteGuard.EnsureNotHosted("/home/data/gateway-stats.db (GatewayStatsDatabase)"));
            Assert.Contains("/home/data/gateway-stats.db", ex.Message, StringComparison.Ordinal);
            Assert.Contains("GatewayStatsDatabase", ex.Message, StringComparison.Ordinal);
            Assert.Contains(HostedSqliteGuard.HostedMarkerEnvVar, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=p", ex.Message, StringComparison.Ordinal);

            // A refusal that cannot say what it refused is not worth throwing.
            Assert.Throws<ArgumentException>(() => HostedSqliteGuard.EnsureNotHosted("  "));

            // Self-host: the guard is inert. This is the half that protects the desktop install.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, null);
            Assert.False(HostedSqliteGuard.IsHosted);
            HostedSqliteGuard.EnsureNotHosted("C:/Users/someone/gateway-stats.db (self-host)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, original);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Open every assembly that makes up the hosted Gateway service. A file that is not there is a LOUD
    /// failure, never a quietly smaller scan: the whole value of an allowlist is that it covers everything it
    /// claims to, and an assembly silently skipped would make DT-SQL-1 pass by not having looked.
    /// </summary>
    private static IEnumerable<(string File, ModuleDefinition Module)> LoadHostedGatewayModules()
    {
        var baseDir = Path.GetDirectoryName(typeof(GatewayDatabase).Assembly.Location)!;
        foreach (var file in HostedGatewayAssemblyFiles)
        {
            var path = Path.Combine(baseDir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"DT-SQL: '{file}' is not in the test output directory ('{baseDir}'), so the no-SQLite scan " +
                    "would silently cover less than it claims. Restore the project reference that brings it here.",
                    path);

            yield return (file, ModuleDefinition.ReadModule(path));
        }
    }

    /// <summary>
    /// The first SQLite thing this type touches, or null. Everything a type can reach SQLite THROUGH is
    /// checked - the members its code calls, the fields and locals it holds, and its own signatures - because
    /// a rule that only looked at <c>newobj</c> would miss a store handed its connection by someone else.
    /// </summary>
    private static string? FirstSqliteTouch(TypeDefinition type)
    {
        foreach (var field in type.Fields)
            if (IsSqlite(field.FieldType)) return Describe(field.FieldType);

        foreach (var method in type.Methods)
        {
            if (IsSqlite(method.ReturnType)) return Describe(method.ReturnType);
            foreach (var p in method.Parameters)
                if (IsSqlite(p.ParameterType)) return Describe(p.ParameterType);

            if (!method.HasBody) continue;

            foreach (var local in method.Body.Variables)
                if (IsSqlite(local.VariableType)) return Describe(local.VariableType);

            foreach (var instr in method.Body.Instructions)
            {
                switch (instr.Operand)
                {
                    case MethodReference m when IsSqlite(m.DeclaringType) || IsSqlite(m.ReturnType):
                        return $"{Describe(IsSqlite(m.DeclaringType) ? m.DeclaringType : m.ReturnType)}.{m.Name}";
                    case FieldReference f when IsSqlite(f.DeclaringType) || IsSqlite(f.FieldType):
                        return Describe(IsSqlite(f.DeclaringType) ? f.DeclaringType : f.FieldType);
                    case TypeReference t when IsSqlite(t):
                        return Describe(t);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when this type is a SQLite type: the Microsoft.Data.Sqlite client, the Entity Framework SQLite
    /// provider (including the <c>UseSqlite</c> extension class), or the raw SQLitePCL layer underneath both -
    /// so going one level lower does not get past the rule. Generic arguments, arrays and by-ref wrappers are
    /// unwrapped, so a <c>List&lt;SqliteConnection&gt;</c> counts exactly as the connection does.
    /// </summary>
    private static bool IsSqlite(TypeReference? type)
    {
        if (type is null) return false;

        if (type is GenericInstanceType generic)
        {
            if (IsSqlite(generic.ElementType)) return true;
            foreach (var arg in generic.GenericArguments)
                if (IsSqlite(arg)) return true;
            return false;
        }

        if (type is TypeSpecification spec) return IsSqlite(spec.ElementType);

        var name = type.FullName;
        if (name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)) return true;
        if (name.StartsWith("SQLitePCL", StringComparison.Ordinal)) return true;
        if (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            && name.Contains("Sqlite", StringComparison.Ordinal)) return true;

        return false;
    }

    private static string Describe(TypeReference type) => type.FullName;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
            foreach (var nested in Flatten(t))
                yield return nested;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var inner in Flatten(nested))
                yield return inner;
    }

    private static TypeDefinition TopLevel(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }
}
