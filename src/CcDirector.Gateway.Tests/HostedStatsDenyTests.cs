using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1848, the stats half: the DevThrottle Stats dashboard is DENIED on the hosted Gateway.
///
/// <c>GET /stats/data</c> served, fleet-globally and to any tenant's device key: every repository name the
/// fleet has driven, the per-agent and per-model tallies, the token SPEND, and the hourly activity series.
/// Its handler took no <c>HttpContext</c> at all, so it could not resolve a tenant even in principle - the
/// same signature-level shape as the prompt log.
///
/// WHY A DENY AND NOT A PARTITION. These are PRE-AGGREGATED FLEET TOTALS with no tenant anywhere in the
/// schema, so there is nothing to partition by without a migration and a re-model, and a per-tenant answer
/// would have to be recomputed from an attribution that was never recorded. Inventing one is a
/// half-partition, which this mission has a law against. The concept does not survive either: this is the
/// OWNER'S private view of HIS OWN gateway, and on shared hosted infrastructure "the owner" is not a thing,
/// so there is no correct per-tenant answer to serve - only a disclosure to close.
///
/// IT IS A REFUSAL, NOT AN EMPTY DASHBOARD. Serving zeroed series would be a FALSE statement rather than an
/// absent one - the same mistake /healthz made when it zeroed its fleet counts and anything monitoring them
/// read a permanently dead fleet. The refusal body is asserted as an EXACT PROPERTY SET - one error field and
/// nothing else - rather than as an absence of known payload keys. A deny-list of today's keys was tried and
/// review broke it in one move; see the note on that test for why an allow-list is the only shape that holds.
///
/// ONE GROUP FILTER, NOT A GUARD PER ROUTE. The refusal is an endpoint filter on the group both routes are
/// mapped into, so it runs before every route in that group INCLUDING ROUTES THAT DO NOT EXIST YET. A guard
/// repeated in each handler rots by construction: the moment somebody adds a route to that file it is
/// undefended and nothing fails. <see cref="HostedStatsGroupFilterTests"/> proves the difference by mapping a
/// brand-new probe route onto the group and finding it already refused with no deny written for it.
///
/// REVERT-PROOF - THE RECIPE ACTUALLY RUN, against the final head, on a box verified to have no other
/// Gateway suite executing. In <c>src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs</c> DELETE the
/// <c>app.AddEndpointFilter(...)</c> block outright, leaving <c>var app = outer.MapGroup("");</c> in place so
/// the group still exists and the file still compiles - the hosted deny is then absent ENTIRELY, with no
/// per-route guard put back in its place. Deleting is the only correct way to revert here: wrapping the
/// filter in <c>if (false)</c> leaves unreachable code, which is a BUILD ERROR in this repository, and a test
/// run after a failed build silently executes the previous binary and reports a false pass. Rebuild, CONFIRM
/// ZERO ERRORS, then run the FULL suite - not a filter over this class. A filtered revert-proof can only
/// answer "do my new tests fire?"; it cannot see whether some existing test already covered the behaviour,
/// and it cannot see collateral damage elsewhere.
///
/// Observed with the filter deleted, on a box verified to have no other Gateway-assembly run anywhere in the
/// process tree: 9 failed, 2947 passed, 10 skipped, 2966 total. The 9 decomposed as the 8 guard canaries
/// below plus one pre-existing failure unrelated to this change
/// (<c>Account.HostedAccountStatusTests.Enrolled_without_a_recorded_email_is_signed_in_with_the_identity_absent</c>),
/// which reproduced on unmodified origin/main at that time and has since been FIXED by #1916. A revert run
/// repeated at or after #1916 should therefore show exactly the 8 below and nothing else - that failure is no
/// longer an allowance, and if it appears it is a real defect. The 8:
///
///   HostedStatsDenyTests.The_stats_feed_is_refused_to_an_enrolled_tenant
///   HostedStatsDenyTests.The_refusal_carries_nothing_but_the_refusal (both cases)
///   HostedStatsDenyTests.The_stats_page_is_refused_too
///   HostedStatsDenyTests.The_refusal_is_not_a_zeroed_dashboard
///   HostedStatsGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own
///   HostedStatsGroupFilterTests.Both_stats_routes_are_refused_on_hosted_through_the_group_filter (both cases)
///
/// TWO THINGS THAT RUN PROVED BEYOND "the tests fire". No test ANYWHERE ELSE in the suite reddened, so this
/// behaviour was NOT already covered - before this change nothing in the Gateway suite would have noticed the
/// stats dashboard serving fleet-global totals on hosted. And there was no collateral red, so the guard is
/// not entangled with unrelated behaviour.
///
/// The unauthenticated control, <see cref="HostedStatsGroupFilterTests.A_route_outside_the_group_still_serves_on_hosted"/>
/// and the whole of <see cref="HostedStatsSelfHostControlTests"/> stayed GREEN through the revert - they are
/// the controls, and a control that moves with the change under test is not a control. Restore the block,
/// rebuild, run the full suite again, confirm green.
///
/// SELF-HOST IS PROVED, NOT INHERITED. <see cref="HostedStatsSelfHostControlTests"/> EXPLICITLY clears
/// <c>CC_GATEWAY_HOSTED</c> and asserts both routes still serve their real payloads. Leaning on
/// <see cref="StatsPageEndpointTests"/> would prove nothing about self-host, because those rest on the test
/// runner's ambient default: if that default ever flips they keep passing while self-host is broken.
/// </summary>
public sealed class HostedStatsDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-stats-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that even
        // this one is refused: there is no credential that makes the owner's private dashboard correct here.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task The_stats_feed_is_refused_to_an_enrolled_tenant()
    {
        var resp = await Get("stats/data", _key);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("stats/data")]
    [InlineData("stats")]
    public async Task The_refusal_carries_nothing_but_the_refusal(string path)
    {
        // AN ALLOW-LIST, NOT A DENY-LIST, and the difference is the whole test.
        //
        // This first asserted that a handful of known payload keys were absent. Review broke it in one move:
        // a denial envelope carrying generatedAtUtc, timeZone, concurrency and notCaptured passed all five
        // tests. Worse, the real feed has keys a substring deny-list silently misses - "tokenSpendByHour"
        // does not contain the string "tokenSpend" once the closing quote is included - so a data-bearing
        // partial dashboard could pass while every listed key was "absent".
        //
        // A deny-list also rots by construction: it protects against the payload as it is TODAY, and every
        // field added to the feed later is unprotected until someone remembers to add it here. Asserting the
        // property set is EXACTLY one error field inverts that - anything empty-shaped, anything new, and
        // anything metadata-looking reddens automatically without this test being touched.
        var resp = await Get(path, _key);
        var body = await resp.Content.ReadAsStringAsync();

        // ASSERT THE STATUS AND THE MEDIA TYPE BEFORE PARSING, so this test reddens as a STATEMENT rather
        // than as a crash. Without these two lines, deleting the guard makes /stats serve the HTML dashboard
        // and JsonDocument.Parse dies with "'<' is an invalid start of a value" - the test still goes red, but
        // it goes red as a JsonReaderException, which proves only that the mutation broke something upstream.
        // A crash cannot tell you WHAT was served in place of the refusal; an assertion can, and that is the
        // difference between a revert-proof and a coincidence.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal("the stats dashboard is not available on the hosted gateway",
            doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_stats_page_is_refused_too()
    {
        // The page and the feed are one surface. Refusing the JSON while still serving the dashboard shell
        // would leave the disclosure surface half-closed and look closed from the outside.
        var resp = await Get("stats", _key);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("Your Throttle", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refusal_is_not_a_zeroed_dashboard()
    {
        // The /healthz lesson, applied here on purpose: a zeroed count is a FALSE statement where an absent
        // one is merely absent. A caller must not be handed a dashboard that reads "no work has been done".
        // The exact-property-set test above is what actually enforces this; this one states the intent in the
        // terms the mistake was originally made in, so the reason survives even if the shape of the check changes.
        var body = await (await Get("stats/data", _key)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("\"turns\":0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"characters\":0", body, StringComparison.Ordinal);
        Assert.Contains("not available", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the route up as a side effect of running before the gate.
        // Without a key the host-wide auth middleware still refuses first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("stats/data")).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// Boots ONLY the stats group on an ephemeral port, exactly as <see cref="StatsPageEndpointTests"/> does, and
/// hands the caller the route group back so a test can map routes onto it. That is what makes the
/// future-route proof possible at all: the group is created inside <c>StatsPageEndpoint.Map</c>, so nothing
/// outside that method could otherwise state a property about routes added to it.
/// </summary>
internal static class StatsGroupProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        GatewayInputStatsAggregator aggregator,
        Action<RouteGroupBuilder>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = StatsPageEndpoint.Map(app, aggregator);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    /// <summary>
    /// Asserts the body is the hosted refusal and NOTHING ELSE, by parsing the JSON and comparing the whole
    /// property set to a one-name allow-list. A substring check cannot see an extra leaked field; enumerating
    /// the property set reddens automatically on anything extra, without this file being touched.
    /// </summary>
    public static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();

        // Media type first, for the same reason as above: if the guard is gone and the route serves the HTML
        // dashboard, this must redden as "expected application/json, got text/html" and not as a JSON parser
        // exception. A red that is a crash proves the mutation landed, not that the guard was what held.
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal("the stats dashboard is not available on the hosted gateway",
            doc.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal is a filter on the stats route GROUP, so it covers
/// routes that have not been written yet.
///
/// A guard line repeated in every handler passes exactly the same tests as a group filter for the routes that
/// exist today, which is precisely why it is dangerous - the difference only shows up on the route somebody
/// adds NEXT, when it is open by default and nothing fails. That difference is not observable by driving
/// /stats and /stats/data, so this class maps a BRAND-NEW probe route onto the group and asserts it is
/// refused with no deny of its own written anywhere. That single test is the one that distinguishes the two
/// implementations, and it is the reason <c>StatsPageEndpoint.Map</c> returns its group.
///
/// The revert recipe and the full expected-red list live on <see cref="HostedStatsDenyTests"/>.
/// </summary>
public sealed class HostedStatsGroupFilterTests : IDisposable
{
    private const string ProbePayloadSentinel = "probe-payload-that-must-never-be-served-on-hosted";

    private readonly string _dir;
    private readonly string? _priorHosted;

    public HostedStatsGroupFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself rather
        // than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private GatewayInputStatsAggregator Aggregator() =>
        new(Path.Combine(_dir, "s-" + Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in
    /// <c>StatsPageEndpoint</c> mentions this path, and no guard is written for it here - the only thing
    /// standing between the caller and the probe payload is the group filter. Delete the filter and this test
    /// serves the probe payload with a 200, which is the future-route hole stated out loud.
    /// </summary>
    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own()
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(
            Aggregator(),
            mapIntoGroup: group => group.MapGet("/stats/added-after-the-deny-was-written",
                () => Results.Json(new { probe = ProbePayloadSentinel })));
        try
        {
            var resp = await http.GetAsync("/stats/added-after-the-deny-was-written");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            await StatsGroupProbeHost.AssertBodyIsNothingButTheRefusal(resp);
            Assert.DoesNotContain(ProbePayloadSentinel, await resp.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// The two production routes, refused through that same filter rather than through a guard of their own.
    /// </summary>
    [Theory]
    [InlineData("/stats")]
    [InlineData("/stats/data")]
    public async Task Both_stats_routes_are_refused_on_hosted_through_the_group_filter(string path)
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(Aggregator());
        try
        {
            var resp = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            await StatsGroupProbeHost.AssertBodyIsNothingButTheRefusal(resp);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// CONTROL: the filter is scoped to the stats group, not a blanket refusal on the whole application. A
    /// route mapped OUTSIDE the group still serves on hosted, so the passing tests above are the filter
    /// doing its job and not the host refusing everything.
    /// </summary>
    [Fact]
    public async Task A_route_outside_the_group_still_serves_on_hosted()
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(
            Aggregator(),
            mapOutsideGroup: routes => routes.MapGet("/not-a-stats-route", () => Results.Json(new { ok = true })));
        try
        {
            var resp = await http.GetAsync("/not-a-stats-route");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("true", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL, STATED EXPLICITLY.
///
/// Self-host is the control for this entire hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. <see cref="StatsPageEndpointTests"/> does not prove it: those tests never mention
/// <c>CC_GATEWAY_HOSTED</c> and pass only because the runner happens to leave it unset. If that ambient
/// default ever flipped - one leaked environment variable, one continuous-integration image change, one test
/// that forgot to restore it - they would keep passing while self-host was completely broken, because they
/// assert nothing about which mode they are in.
///
/// So this class sets the variable itself, to BOTH non-hosted values that occur in practice: absent, and
/// present-but-not-"1". It then asserts the routes serve their REAL PAYLOADS - the seeded counts, the ranked
/// repository, the honesty caveats, the dashboard markup - and not merely that the refusal string is absent.
/// An empty-but-successful response would satisfy "the refusal is absent" while still being a broken
/// self-host, so absence of the refusal is not the assertion.
///
/// These tests must stay GREEN through the revert described on <see cref="HostedStatsDenyTests"/>. A control
/// that moves with the change under test is not a control.
/// </summary>
public sealed class HostedStatsSelfHostControlTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _priorHosted;

    public HostedStatsSelfHostControlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-selfhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Puts the process into a stated non-hosted mode and proves it took, so no test below can silently be
    /// running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    private GatewayInputStatsAggregator SeededAggregator()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s-" + Guid.NewGuid().ToString("N") + ".db"));
        agg.Observe(new SessionDto
        {
            SessionId = "s1",
            RepoPath = @"D:\ReposFred\devthrottle",
            InputStats = new InputStatsDto
            {
                Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = 7, Characters = 700 } },
            },
        });
        return agg;
    }

    /// <summary>
    /// null = the variable is absent. "0" = the variable is present and explicitly not hosted. Both are real
    /// non-hosted deployments and both must serve.
    /// </summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_stats_page_still_serves_its_real_dashboard_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await StatsGroupProbeHost.StartAsync(SeededAggregator());
        try
        {
            var resp = await http.GetAsync("/stats");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.StartsWith("text/html", resp.Content.Headers.ContentType!.ToString());

            // The REAL page, asserted by what it contains - not by the refusal being absent. An empty 200
            // would pass "no refusal" and still be a dead dashboard.
            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Your Throttle", html);
            Assert.Contains("/stats/data", html);
            Assert.Contains("What you have spent", html);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_stats_feed_still_serves_its_real_totals_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await StatsGroupProbeHost.StartAsync(SeededAggregator());
        try
        {
            var resp = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            // The refusal envelope has exactly one property named "error". Proving the real feed means
            // proving the payload it actually carries is here, one field at a time.
            var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.DoesNotContain("error", properties);
            foreach (var expected in new[]
                     {
                         "buckets", "hourlyTurns", "wingman", "repos", "agents", "models",
                         "tokenSpend", "tokenSpendByHour", "tokenSpendByModel", "notCaptured",
                     })
                Assert.Contains(expected, properties);

            // The seeded numbers themselves, so an empty-but-shaped payload cannot pass as "serving".
            var bucket = Assert.Single(root.GetProperty("buckets").EnumerateArray());
            Assert.Equal("voice", bucket.GetProperty("modality").GetString());
            Assert.Equal("phone", bucket.GetProperty("surface").GetString());
            Assert.Equal(7, bucket.GetProperty("turns").GetInt64());
            Assert.Equal(700, bucket.GetProperty("characters").GetInt64());

            var repo = Assert.Single(root.GetProperty("repos").EnumerateArray());
            Assert.Equal("devthrottle", repo.GetProperty("repoName").GetString());
            Assert.Equal(7, repo.GetProperty("turns").GetInt64());

            Assert.True(root.GetProperty("notCaptured").GetArrayLength() > 0);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// THE SECOND HALF OF THE FUTURE-ROUTE PROOF, and the review asked for both halves by name: the probe
    /// route must be REFUSED on hosted and SERVED with hosted mode EXPLICITLY off. The hosted half is
    /// <see cref="HostedStatsGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own"/>;
    /// this is its mirror, on the SAME probe path, with hosted mode stated rather than assumed - and stated in
    /// both non-hosted forms, absent and present-but-"0".
    ///
    /// Without this half, "the filter refuses everything, always" would pass every hosted test in this file
    /// while having silently killed the route for self-host too. One direction alone cannot tell a working
    /// gate apart from a brick.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await StatsGroupProbeHost.StartAsync(
            SeededAggregator(),
            mapIntoGroup: group => group.MapGet("/stats/added-after-the-deny-was-written",
                () => Results.Json(new { probe = "served" })));
        try
        {
            var resp = await http.GetAsync("/stats/added-after-the-deny-was-written");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("served", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
