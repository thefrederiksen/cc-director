using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE CONTEXT-LESS ROUTE CENSUS, CLOSED AND EXECUTABLE (tenant-boundary hardening, release
/// 2026-07-31, the brief's item 5).
///
/// A route that takes a path parameter but NO <see cref="HttpContext"/> cannot read the caller's
/// request, so it cannot resolve a tenant from it. The original census counted such routes, probed a
/// handful, and refused to generalise - correctly, because eight samples cannot stand for a family. The
/// gap it left was not a missing test but a missing INVENTORY: nobody could say what the whole set was,
/// so nobody could say the whole set was safe.
///
/// This file closes that by making the inventory a TEST rather than a paragraph. It reads the FINALISED
/// route table from a real <see cref="GatewayHost"/> - the actual endpoints, with route-group prefixes
/// applied, and each handler's real parameter list read by reflection - and asserts the context-less set
/// is EXACTLY the ruled list below. Every entry in that list carries a written verdict naming the
/// mechanism that keeps one tenant out of another's data, and the report for this phase carries the
/// evidence per row.
///
/// WHY THIS SHAPE, AND NOT A PROSE TABLE. A table in a document is true on the day it is written. This
/// fails the moment a new context-less route is mapped, so the next person cannot add one without
/// reaching a verdict about it - which is the property the census was supposed to buy and could not,
/// because it was prose. It also cannot be fooled by a source-parsing mistake: patterns come from the
/// route table, not from reading Map calls, so a route mounted under a group prefix is counted at its
/// real path (the earlier source-derived attempt read /vault/keys/{name} as /{name} and would have
/// mis-stated the census).
///
/// BOTH DEPLOYMENTS ARE COUNTED, because they map different route tables. On HOSTED, the
/// <c>HostedRouteDeny</c> families (the key vault, the developer exe slots) are not mapped at all - a
/// verb-less refusal catch-all claims their prefixes - so their routes cannot serve any tenant. That is
/// a verdict this test EXECUTES rather than asserts on faith: the hosted case must not contain them.
/// </summary>
public sealed class ContextLessRouteCensusTests
{
    private readonly ITestOutputHelper _out;

    public ContextLessRouteCensusTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The census of context-less routes on the HOSTED Gateway - the multi-tenant deployment, and so the
    /// only one where cross-tenant reach is possible at all. Each row's verdict:
    ///
    /// EF global tenant filter (the ambient request scope entered by the device-key middleware, plus
    /// GatewayDbContext's per-entity query filter; GatewayDatabase throws rather than defaulting when no
    /// scope exists). Executed cross-tenant for every family below - see the phase report's table for
    /// which test covers which row:
    ///   /cron/jobs/{id} (+ DELETE, /runs)          cron_jobs, cron_runs
    ///   /gateway/governance/session-spend/{id}     session_spend
    ///   /lists/{name} (+ /consumer, /items/...)    worklists, worklist_items
    ///   /gateway/workflows/{id} and its family     workflows, workflow_versions, workflow_files,
    ///                                              workflow_tenant_overrides
    ///   /gateway/workflow-runs/{id}                workflow_runs
    ///   /gateway/skills/{id} and its family        skills, skill_versions, skill_files,
    ///                                              skill_tenant_overrides
    ///   /gateway/rules/{id:guid} (+ /firings)      session_rules, session_rule_firings
    ///
    /// The rules family was added by the Session Rules mission and reached this census LATE - it shipped
    /// while this suite was parked, so these three rows were missing and this test was red on main with
    /// nobody watching. Its verdict is the same seam as the skills and workflow families: both tables
    /// derive from GatewayMintedKeyEntity (tenant-scoped, and the key is Gateway-minted so no caller can
    /// present a rule id) and both carry the entity global query filter. GET and DELETE
    /// /gateway/rules/{id} are EXECUTED cross-tenant by
    /// CensusRouteTenancyProbeTests.SessionRules_AreNotReachableAcrossTenantsEvenHoldingTheOtherTenantsRuleId.
    /// GET /gateway/rules/{id}/firings is NOT executed cross-tenant and that is stated rather than
    /// implied: no route can write a firing (only the evaluator does), so both tenants read an empty list
    /// and two empty lists prove nothing. Its verdict is a code read - same store, same filter.
    ///
    /// POST /gateway/rules/draft and POST /gateway/rules/{id}/promote are deliberately NOT in this census:
    /// draft takes no path parameter, and promote takes the HttpContext (it mints the promotion grant from
    /// the authenticated request), so neither is context-less.
    ///
    /// Hosted deny (the legacy same-machine discovery plane - not a tenant surface at all; refused on
    /// hosted, gated on the process-level hosted flag and proven by
    /// <see cref="NullBoundaryHostedGateFailClosedTests"/>):
    ///   POST /directors/{id}/doorbell, DELETE /directors/{id}/registration
    /// </summary>
    private static readonly string[] HostedCensus =
    {
        "DELETE /cron/jobs/{id}",
        "DELETE /directors/{id}/registration",
        "DELETE /gateway/rules/{id:guid}",
        "DELETE /gateway/skills/{id}",
        "DELETE /gateway/workflows/{id}",
        "DELETE /lists/{name}/consumer",
        "DELETE /lists/{name}/items/{source}/{id}",
        "GET /cron/jobs/{id}",
        "GET /cron/jobs/{id}/runs",
        "GET /gateway/governance/session-spend/{sessionId}",
        "GET /gateway/rules/{id:guid}",
        "GET /gateway/rules/{id:guid}/firings",
        "GET /gateway/skills/{id}",
        "GET /gateway/skills/{id}/body",
        "GET /gateway/skills/{id}/files/{**filePath}",
        "GET /gateway/skills/{id}/versions",
        "GET /gateway/skills/{id}/versions/{version:int}",
        "GET /gateway/workflow-runs/{id:guid}",
        "GET /gateway/workflows/{id}",
        "GET /gateway/workflows/{id}/files/{fileName}",
        "GET /gateway/workflows/{id}/instructions",
        "GET /gateway/workflows/{id}/versions",
        "GET /gateway/workflows/{id}/versions/{version:int}",
        "GET /lists/{name}",
        "POST /directors/{id}/doorbell",
        "POST /gateway/skills/{id}/clone",
        "POST /gateway/skills/{id}/disable",
        "POST /gateway/skills/{id}/enable",
        "POST /gateway/skills/{id}/publish",
        "POST /gateway/workflows/{id}/clone",
        "POST /gateway/workflows/{id}/disable",
        "POST /gateway/workflows/{id}/enable",
        "POST /gateway/workflows/{id}/publish",
    };

    /// <summary>
    /// The two families that exist ONLY off hosted, because <c>HostedRouteDeny</c> takes them off the
    /// hosted route table entirely. Their verdict is therefore "not reachable on the multi-tenant
    /// deployment", and the hosted assertion proves it by their ABSENCE there:
    ///   GET/DELETE /vault/keys/{name}      - the key vault (family "key-vault")
    ///   DELETE /exes/slots/{n}, POST /exes/slots/{n}/build-start - the developer exe slots
    ///     (family "exes-slots"); additionally mapped only on Windows.
    /// On self-host both are single-owner surfaces behind the host-wide credential gate.
    /// </summary>
    private static readonly string[] SelfHostOnlyExtras =
    {
        "DELETE /exes/slots/{n}",
        "DELETE /vault/keys/{name}",
        "GET /vault/keys/{name}",
        "POST /exes/slots/{n}/build-start",
    };

    [Fact]
    public async Task The_hosted_context_less_route_set_is_exactly_the_ruled_census()
    {
        var actual = await ContextLessRoutes(hosted: true);
        foreach (var row in actual) _out.WriteLine(row);

        // If this fails, a context-less route was added, removed, or re-pathed on the HOSTED Gateway.
        // That is not automatically a defect - but it IS a verdict nobody has reached yet. Establish
        // which store the route reaches and what confines it to the caller's tenant, write that verdict
        // into the doc comment above, add a cross-tenant probe if the family has none, and only then
        // add the row here.
        Assert.Equal(HostedCensus, actual);
    }

    [Fact]
    public async Task The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families()
    {
        var actual = await ContextLessRoutes(hosted: false);
        foreach (var row in actual) _out.WriteLine(row);

        var expected = HostedCensus.Concat(SelfHostOnlyExtras).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);

        // The point of the pair: the key vault and the developer exe slots are context-less routes over
        // process-global stores with no tenant column at all, and they are reachable on self-host and
        // NOT on hosted. This asserts that against the OBSERVED hosted route table - re-read here rather
        // than compared against the ruled constant above, because comparing two constants would be an
        // assertion this file cannot fail. Each extra must be present in the self-host table (it is, by
        // the equality above) and ABSENT from the hosted one.
        var hostedActual = await ContextLessRoutes(hosted: true);
        foreach (var row in SelfHostOnlyExtras)
        {
            Assert.Contains(row, actual);
            Assert.DoesNotContain(row, hostedActual);
        }
    }

    /// <summary>
    /// The finalised route table of a real host, reduced to the context-less routes: a path parameter in
    /// the pattern, and no <see cref="HttpContext"/> in the handler's parameter list. The hosted refusal
    /// catch-alls are excluded by name - they carry no handler method at all and exist precisely to make
    /// a denied family unreachable, so counting them as census rows would invert their meaning.
    /// </summary>
    private static async Task<string[]> ContextLessRoutes(bool hosted)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var dir = Path.Combine(Path.GetTempPath(), "cc-census-routes-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "census-token",
            authEnabled: true,
            instancesDirectory: dir,
            workListsPath: Path.Combine(dir, "worklists", "worklists.json"));
        try
        {
            await gateway.StartAsync();

            return gateway.MappedEndpoints.OfType<RouteEndpoint>()
                .Select(e => new
                {
                    Pattern = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'),
                    Methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>(),
                    Handler = e.Metadata.GetMetadata<MethodInfo>(),
                })
                .Where(r => r.Pattern.Contains('{', StringComparison.Ordinal))
                .Where(r => !r.Pattern.Contains("hostedDeniedPath", StringComparison.Ordinal))
                .Where(r => r.Handler is not null
                            && !r.Handler.GetParameters().Any(p => p.ParameterType == typeof(HttpContext)))
                .SelectMany(r => r.Methods.DefaultIfEmpty("ANY"), (r, m) => $"{m} {r.Pattern}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
