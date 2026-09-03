using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// EVERY MAPPED ROUTE OF THE SESSION-RULES SURFACE HAS BEEN CLASSIFIED BY <see cref="SessionKeyGuard"/> -
/// allowed to a session key, or refused to one on purpose. There is no third state, and a route that
/// reaches this application without somebody reaching a verdict about it makes this test red.
///
/// THE DEFECT THIS EXISTS FOR, OBSERVED RATHER THAN IMAGINED. The rules surface shipped with none of its
/// routes on the guard's allow list, so every call from an agent's command line came back HTTP 403
/// <c>session_key_out_of_scope</c> - the whole feature was unreachable to the credential it was built for.
/// Every suite was green throughout. It could not have been otherwise: the guard's own unit tests are
/// written against the guard's own list, and the command line's tests mock the transport, so nothing
/// anywhere connected "a route was added" to "the guard was told about it". The same defect had already
/// happened twice on this file, to the skill catalogue and to the schedules, and each time it was fixed by
/// adding rows to a hand-kept list - which fixes the instance and leaves the mechanism running.
///
/// SO THIS TEST DERIVES ITS SUBJECT FROM THE BUILT APPLICATION, NEVER FROM A LIST. It reads the finalised
/// route table off a real <see cref="GatewayHost"/> - the same instrument
/// <see cref="ContextLessRouteCensusTests"/> uses, and for the same reason: patterns come from the router
/// with group prefixes already applied, so a route cannot be missed by a mistake in reading source. Add a
/// route under <c>/gateway/rules</c> and this test fails until the guard says, in its literal switch, what
/// an agent may do with it.
///
/// WHAT IT DOES NOT PROVE. It says every route has a verdict; it does not say the verdicts are the right
/// ones. Whether an agent SHOULD be able to delete a rule is the owner's ruling and lives in the guard's
/// own comments and in the guard's own unit tests. This test is the mechanism, not the judgement.
/// </summary>
public sealed class SessionRuleRouteGuardCensusTests
{
    private readonly ITestOutputHelper _out;

    public SessionRuleRouteGuardCensusTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_mapped_session_rule_route_is_classified_by_the_session_key_guard(bool hosted)
    {
        var routes = await RuleRoutes(hosted);

        // THE INSTRUMENT CHECK, FIRST. The assertion below is "nothing was unclassified", and a pass
        // condition that is an absence certifies a run that never happened: if this enumeration returned
        // an empty set - a renamed host, a route table that failed to build, a filter typo - the loop
        // would find no unclassified route and report success without having looked at anything. So the
        // presence of the surface is asserted before its contents are judged.
        Assert.NotEmpty(routes);
        _out.WriteLine($"hosted={hosted}: {routes.Count} mapped routes under /gateway/rules");

        var unclassified = new List<string>();
        var allowed = new List<string>();
        var refused = new List<string>();

        foreach (var (method, pattern) in routes)
        {
            var ruling = SessionKeyGuard.ClassifyRuleRoute(method, ProbePath(pattern));
            _out.WriteLine($"{ruling,-16} {method} {pattern}");

            switch (ruling)
            {
                case RuleRouteRuling.Allowed: allowed.Add($"{method} {pattern}"); break;
                case RuleRouteRuling.RefusedOnPurpose: refused.Add($"{method} {pattern}"); break;
                default: unclassified.Add($"{method} {pattern}"); break;
            }
        }

        // If this fails, a route was added to the session-rules surface and nobody decided what an agent
        // may do with it. The guard denies it today - it is an allow list - but that denial is the default
        // falling through rather than a decision, and the agent hitting it reads a 403 that says nothing
        // true about why. Classify it in SessionKeyGuard.RuleRoute, allowed or refused, and say why there.
        Assert.Empty(unclassified);

        // Both verdicts must actually occur. Without this, deleting every arm of the guard's switch would
        // leave a surface that is entirely unclassified (caught above) - but collapsing the switch to one
        // blanket verdict would not be caught at all, and a blanket "allowed" is precisely the prefix rule
        // the guard exists to avoid.
        Assert.NotEmpty(allowed);
        Assert.NotEmpty(refused);
    }

    /// <summary>
    /// A concrete path a guard can be asked about, built from a route pattern by replacing each parameter
    /// segment with a real identifier. The guard reads structure and never the identifier itself, so any
    /// value does; a genuine identifier is used anyway so the probe is a path the router would accept.
    /// </summary>
    private static string ProbePath(string pattern)
    {
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => seg.Contains('{', StringComparison.Ordinal)
                ? "6b2f4b1e-6d5a-4a2f-9f5f-9d7c0f3a1b2c"
                : seg);
        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// The finalised route table of a real host, reduced to the routes under <c>/gateway/rules</c>. The
    /// hosted refusal catch-alls are excluded by name exactly as the context-less census excludes them:
    /// they carry no handler and exist to make a denied family unreachable, so counting one would invert
    /// its meaning.
    /// </summary>
    private static async Task<List<(string Method, string Pattern)>> RuleRoutes(bool hosted)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var dir = Path.Combine(Path.GetTempPath(), "cc-rule-guard-census-" + Guid.NewGuid().ToString("N"));
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
                })
                .Where(r => r.Pattern.Equals("/gateway/rules", StringComparison.OrdinalIgnoreCase)
                            || r.Pattern.StartsWith("/gateway/rules/", StringComparison.OrdinalIgnoreCase))
                .Where(r => !r.Pattern.Contains("hostedDeniedPath", StringComparison.Ordinal))
                .SelectMany(r => r.Methods.DefaultIfEmpty("ANY"), (r, m) => (Method: m, r.Pattern))
                .Distinct()
                .OrderBy(r => r.Pattern, StringComparer.Ordinal)
                .ThenBy(r => r.Method, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
