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
/// Revert-prove: delete the <c>DenyOnHosted()</c> guard from either route and that route's tests go RED -
/// the data route with a 200 carrying the fleet-global keys, the page route with the HTML dashboard.
///
/// Self-host is the control and is covered by <see cref="StatsPageEndpointTests"/>, which run with hosted
/// mode off and are untouched by this change.
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
