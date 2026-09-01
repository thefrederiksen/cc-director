using System.Data.Common;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The prompt-delete erasure runs as BULK statements - two UPDATEs and a DELETE that never load a row -
/// and its tenant scoping comes entirely from the global query filter on the model. On SQLite that is
/// proved behaviourally: remove the filter and another account's rows are erased. This class answers the
/// remaining question, which is the one whose failure would be worst: **does that predicate survive
/// translation to the provider the hosted Gateway actually runs on?**
///
/// If it did not, a single member's delete would erase EVERY account's history in one statement. Nothing
/// else in this feature has a failure that large.
///
/// HOW, and what this is and is not:
///
///  - It drives <see cref="SessionHistoryStore.EraseWithin"/> - the PRODUCT's own statements - against a
///    real Npgsql-configured <see cref="GatewayDbContext"/>, and captures the command text Entity
///    Framework hands the provider. A test that re-typed the LINQ would be proving the copy.
///  - It never reaches a server. Two interceptors suppress the connection open and the command execution,
///    so the SQL is generated, captured, and thrown away. That is deliberate: the question is what Entity
///    Framework GENERATES, and answering it needed neither a rig nor an hour of the machine-wide lock.
///  - It therefore proves TRANSLATION, not EXECUTION. It does not prove PostgreSQL accepts or performs
///    these statements - only that the tenant predicate is in the WHERE clause of each. Executing them
///    against a real server is a separate proof and this class does not stand in for it.
/// </summary>
public sealed class TheErasureCarriesTheTenantPredicateOnNpgsqlTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Every captured statement is written to the test's output, so the generated SQL lands in the
    /// TRX and a reader can check the assertion against the text rather than take it on trust.</summary>
    public TheErasureCarriesTheTenantPredicateOnNpgsqlTests(Xunit.Abstractions.ITestOutputHelper output)
        => _output = output;

    /// <summary>
    /// A well-formed connection string to nowhere. Nothing connects: see the interceptors.
    ///
    /// The host is a name in the reserved <c>.invalid</c> top-level domain, which is guaranteed never to
    /// resolve. It used to be a loopback address, and the reserved name says "nowhere" BETTER: a loopback
    /// address names a real machine that may well have something listening on it, so a string claiming to
    /// be unreachable was in fact the one address most likely to answer if an interceptor ever regressed.
    /// The architecture guard in <c>NoCrossMachineLoopbackGuardTests</c> is what surfaced it.
    /// </summary>
    private const string NeverConnected = "Host=nowhere.invalid;Port=1;Database=devthrottle;Username=u;Password=p";

    private const string Tenant = "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b";

    /// <summary>One captured statement: the text Entity Framework generated and the values it bound.</summary>
    private sealed record Statement(string Sql, IReadOnlyList<object?> Parameters);

    /// <summary>Captures every command and suppresses its execution, so no server is needed.</summary>
    private sealed class CaptureAndSuppressCommands : DbCommandInterceptor
    {
        public List<Statement> Captured { get; } = new();

        private void Capture(DbCommand command)
            => Captured.Add(new Statement(command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList()));

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Capture(command);
            return InterceptionResult<int>.SuppressWithResult(0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new EmptyScalarReader());
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Capture(command);
            return InterceptionResult<object>.SuppressWithResult(0);
        }
    }

    /// <summary>Suppresses the connection open, so the suppressed commands never need a live socket.</summary>
    private sealed class SuppressConnectionOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();
    }

    /// <summary>One row of one zero, which is all the count query needs to get an answer and move on.</summary>
    private sealed class EmptyScalarReader : DbDataReader
    {
        private int _row;
        public override bool Read() => _row++ == 0;
        public override int FieldCount => 1;
        public override object GetValue(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override bool IsDBNull(int ordinal) => false;
        public override bool HasRows => true;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => 0;
        public override object this[string name] => 0;
        public override bool NextResult() => false;
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "integer";
        public override DateTime GetDateTime(int ordinal) => default;
        public override decimal GetDecimal(int ordinal) => 0;
        public override double GetDouble(int ordinal) => 0;
        public override Type GetFieldType(int ordinal) => typeof(int);
        public override float GetFloat(int ordinal) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override string GetName(int ordinal) => "Value";
        public override int GetOrdinal(string name) => 0;
        public override string GetString(int ordinal) => "";
        public override int GetValues(object[] values) { values[0] = 0; return 1; }
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    private static GatewayDbContext NewNpgsqlContext(CaptureAndSuppressCommands capture)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(NeverConnected, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .AddInterceptors(capture, new SuppressConnectionOpen())
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = Tenant };
    }

    [Fact]
    public void Every_statement_the_erasure_issues_names_the_tenant_in_its_where_clause()
    {
        var capture = new CaptureAndSuppressCommands();
        using (var ctx = NewNpgsqlContext(capture))
        {
            SessionHistoryStore.EraseWithin(ctx);
        }

        // The count, the clearing update, the roll-up delete. Asserted so that a future change which adds a
        // statement cannot slip past the per-statement check below by simply not being looked at. It was
        // four until the seal exemption was reversed: the prompt line and the summary needed separate
        // updates only while sealed rows were spared, and one predicate covers both now.
        Assert.Equal(3, capture.Captured.Count);
        Assert.Single(capture.Captured, s => s.Sql.TrimStart().StartsWith("SELECT count", StringComparison.OrdinalIgnoreCase));
        Assert.Single(capture.Captured, s => s.Sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.Single(capture.Captured, s => s.Sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));

        foreach (var statement in capture.Captured)
        {
            _output.WriteLine(statement.Sql);
            _output.WriteLine("-- bound: " + string.Join(", ", statement.Parameters.Select(p => p?.ToString() ?? "null")));
            _output.WriteLine("");

            // The generated text must HAVE a WHERE clause and constrain the tenant column INSIDE it.
            // Asserting the column merely appears somewhere would pass on a statement that only listed it
            // among the columns it sets or selects, which is the failure this test exists to catch.
            var where = statement.Sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            Assert.True(where >= 0, $"no WHERE clause in the generated statement:\n{statement.Sql}");
            Assert.Contains("tenant_id", statement.Sql[where..], StringComparison.Ordinal);

            // And the VALUE bound to it is this tenant. The text alone would be satisfied by a predicate
            // comparing the column to something else entirely; the parameter is what makes it this
            // account's rows and no others.
            Assert.Contains(Tenant, statement.Parameters.Select(p => p as string));
        }
    }

    /// <summary>
    /// The control, without which the fact above is unfalsifiable. A statement built on the SAME context
    /// with the filter deliberately ignored must come out WITHOUT the tenant predicate - proving the
    /// assertion can tell the two apart, rather than passing on any statement that mentions a table.
    /// </summary>
    [Fact]
    public void The_same_statement_without_the_filter_has_no_tenant_predicate_which_is_how_we_know_the_check_works()
    {
        var capture = new CaptureAndSuppressCommands();
        using (var ctx = NewNpgsqlContext(capture))
        {
            ctx.SessionHistoryRollups.IgnoreQueryFilters().ExecuteDelete();
        }

        var statement = Assert.Single(capture.Captured);
        Assert.StartsWith("DELETE", statement.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant_id", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(Tenant, statement.Parameters.Select(p => p as string));
    }
}
