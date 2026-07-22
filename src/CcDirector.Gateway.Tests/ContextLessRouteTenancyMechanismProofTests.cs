using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE MECHANISM, SEPARATED INTO ITS PARTS.
///
/// <see cref="ContextLessDatabaseRouteTenancyTests"/> proves the OUTCOME end-to-end: tenant A naming tenant
/// B's object id on a context-less database route gets an ordinary not-found. That outcome rests on a
/// conjunction of THREE production mechanisms, and an end-to-end test cannot tell them apart - it would pass
/// if any one of them happened to be doing all the work, which is exactly how a proof comes to rest on an
/// assumption nobody has looked at:
///
///   1a. THE AUTH BOUNDARY RESOLVES. A host-wide middleware, registered before routing, reads the
///       AUTHENTICATED device key and resolves that key's tenant (GatewayHost.cs:1517).
///   1b. AND IT ENTERS THE SCOPE IT RESOLVED, around the whole downstream pipeline (GatewayHost.cs:1520).
///       This is a SEPARATE step from 1a and separately bypassable: resolution could be perfectly correct
///       and simply never entered. The two therefore cannot share one mutation arm.
///   2.  THE GLOBAL QUERY FILTER FILTERS. Every tenant-scoped entity carries a global query filter that
///       restricts reads to the ambient tenant (GatewayDbContext.ApplyTenantScope).
///   3.  NO SCOPE MEANS NO DATABASE. Opening a scoped context with no tenant in scope throws by design,
///       rather than defaulting to some tenant (GatewayDatabase.CreateContext reading
///       AsyncLocalTenantContext.Current).
///
/// This file exercises 2 and 3 with NO HTTP and NO MIDDLEWARE AT ALL - the tenant is entered by the test
/// itself - so each one is observed doing its own job in isolation, and neither can be standing in for the
/// other. Step 1a is probed directly against the running boundary in
/// <see cref="ContextLessDatabaseRouteTenancyTests"/>, where the device registry lives. Step 1b - that the
/// middleware actually enters what it resolved - is NOT provable by any test that enters the scope itself,
/// and is established only causally, by removing that call in production; see the pull request.
///
/// Every isolation assertion here carries its own SERVED-SIDE POSITIVE FACT. Proving a row is absent proves
/// nothing on its own - the row may never have been written. So the same query is re-run with the filter
/// deliberately ignored, which shows the row was on disk the whole time and that the FILTER, not a failed
/// seed, is what hid it.
/// </summary>
public sealed class ContextLessRouteTenancyMechanismProofTests : IDisposable
{
    private const string TenantA = "tenant-alice";
    private const string TenantB = "tenant-bob";

    private readonly ITestOutputHelper _out;
    private readonly GatewayDbTestHarness _harness = new();

    public ContextLessRouteTenancyMechanismProofTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// MECHANISM 2, ALONE. One database file, two tenants, no middleware and no HTTP: the tenant is supplied
    /// directly by the test, so nothing about the auth boundary can influence the result.
    ///
    /// The third read is the one that makes this a proof rather than an absence. Tenant A re-runs its own
    /// query with the global filter ignored and finds the row, stamped with tenant B's id - so the row was
    /// present and readable on that very connection, and the ONLY thing that hid it from tenant A was the
    /// query filter. Without that arm, an unwritten row and a filtered row look identical.
    /// </summary>
    [Fact]
    public void TheGlobalQueryFilterAlone_HidesTheOtherTenantsRow_AndIgnoringTheFilterProvesTheRowWasThereAllAlong()
    {
        var name = "bobs-isolated-list-" + Guid.NewGuid().ToString("N")[..8];
        var rowId = Guid.NewGuid();
        const string consumer = "bobs-consumer-token";

        var asBob = _harness.Open(new FixedTenantContext(new TenantId(TenantB)));
        var asAlice = _harness.Open(new FixedTenantContext(new TenantId(TenantA)));

        // Seed one row as tenant B, with values only this test knows.
        using (var ctx = asBob.CreateContext())
        {
            Assert.Equal(TenantB, ctx.ActiveTenant);
            ctx.WorkLists.Add(new WorkListEntity
            {
                Id = rowId,
                Name = name,
                Consumer = consumer,
                TenantId = TenantB,
            });
            Assert.Equal(1, ctx.SaveChanges());
        }

        // POSITIVE CONTROL - the owner reads the exact seeded fingerprint back.
        using (var ctx = asBob.CreateContext())
        {
            var mine = ctx.WorkLists.Where(w => w.Name == name).ToList();
            Assert.Single(mine);
            Assert.Equal(rowId, mine[0].Id);
            Assert.Equal(name, mine[0].Name);
            Assert.Equal(consumer, mine[0].Consumer);
            Assert.Equal(TenantB, mine[0].TenantId);
            _out.WriteLine($"OWNER  (tenant B) sees the row: id={mine[0].Id} name={mine[0].Name} tenant={mine[0].TenantId}");
        }

        // ISOLATION - the other tenant, same file, same query, sees nothing.
        using (var ctx = asAlice.CreateContext())
        {
            Assert.Equal(TenantA, ctx.ActiveTenant);
            var theirs = ctx.WorkLists.Where(w => w.Name == name).ToList();
            _out.WriteLine($"OTHER  (tenant A) sees {theirs.Count} row(s) for the same name");
            Assert.Empty(theirs);

            // THE SERVED-SIDE POSITIVE FACT for that emptiness: the row IS on disk, on THIS connection,
            // and it belongs to tenant B. The emptiness above is therefore the filter's doing and not a
            // seed that never landed.
            var unfiltered = ctx.WorkLists.IgnoreQueryFilters().Where(w => w.Name == name).ToList();
            Assert.Single(unfiltered);
            Assert.Equal(rowId, unfiltered[0].Id);
            Assert.Equal(consumer, unfiltered[0].Consumer);
            Assert.Equal(TenantB, unfiltered[0].TenantId);
            _out.WriteLine($"OTHER  (tenant A) with the filter IGNORED sees it: tenant={unfiltered[0].TenantId}");
        }
    }

    /// <summary>
    /// MECHANISM 3, ALONE, WITH ITS EXACT FINGERPRINT. Over HTTP this failure is a bare 500 with a generic
    /// body, and a status code cannot say WHICH internal error produced it - any unrelated fault would look
    /// the same. Here the exception itself is caught and pinned: its type, and the deny-by-default sentence
    /// its own source carries.
    ///
    /// The positive control is the same call, on the same database, differing only in that a scope has been
    /// entered - and it not only succeeds but carries exactly the entered tenant into the context. So the
    /// throw is attributable to the absence of a scope and to nothing else.
    /// </summary>
    [Fact]
    public void WithNoTenantScope_OpeningAScopedContextFailsClosed_AndInsideAScopeItCarriesExactlyThatTenant()
    {
        var ambient = new AsyncLocalTenantContext();
        var db = _harness.Open(ambient);

        // NO SCOPE - fails closed, and we pin what the failure actually is.
        var ex = Assert.Throws<InvalidOperationException>(() => db.CreateContext());
        _out.WriteLine($"NO SCOPE -> {ex.GetType().FullName}: {ex.Message}");
        Assert.Contains("No tenant is in scope for this hosted operation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("deny-by-default", ex.Message, StringComparison.Ordinal);

        // POSITIVE CONTROL - identical call, one difference: a scope is in effect.
        using (ambient.Enter(new TenantId(TenantB)))
        {
            using var ctx = db.CreateContext();
            Assert.Equal(TenantB, ctx.ActiveTenant);
            _out.WriteLine($"IN SCOPE -> a context opened with ActiveTenant={ctx.ActiveTenant}");
        }

        // And the denial returns the moment the scope is left, so the scope is what carried it.
        var again = Assert.Throws<InvalidOperationException>(() => db.CreateContext());
        Assert.Contains("deny-by-default", again.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SCOPE PRIMITIVE ITSELF - it makes exactly the entered tenant ambient, nests, and restores on the
    /// way out.
    ///
    /// READ THE LIMIT OF THIS TEST CAREFULLY. It proves the primitive WORKS. It does NOT prove that the
    /// production middleware CALLS it - that is a different claim, and no test that enters the scope itself
    /// can establish it. Resolving the caller's tenant and entering that resolved scope are two separately
    /// bypassable production steps (GatewayHost.cs:1517 and :1520), and a mutation that cannot tell them
    /// apart cannot say which one is load-bearing. That the middleware really enters the scope it resolved is
    /// therefore established causally, by removing the EnterScope call in production and watching a distinct
    /// set of tests turn red - device-key requests taking the no-scope path, which is a different signature
    /// from constant resolution. See the pull request's pre-registered mutations.
    /// </summary>
    [Fact]
    public void EnteringAScope_MakesExactlyThatTenantAmbient_Nests_AndRestoresTheDenialOnTheWayOut()
    {
        var ambient = new AsyncLocalTenantContext();
        Assert.Null(ambient.CurrentOrNull);

        using (ambient.Enter(new TenantId(TenantB)))
        {
            Assert.Equal(TenantB, ambient.Current.Value);

            using (ambient.Enter(new TenantId(TenantA)))
                Assert.Equal(TenantA, ambient.Current.Value);

            // The inner scope restored the outer one exactly, rather than clearing it.
            Assert.Equal(TenantB, ambient.Current.Value);
        }

        Assert.Null(ambient.CurrentOrNull);
        Assert.Throws<InvalidOperationException>(() => ambient.Current);
    }
}
