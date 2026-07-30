using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
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
/// Issue #1848 deny retirement: the DevThrottle Stats feed now SERVES the caller's own tenant on the hosted
/// Gateway. The aggregator behind it is tenant-partitioned (MTR-08 - every map keyed by (tenant, ...)), so
/// there is a correct per-tenant answer, and <c>GET /stats/data</c> resolves the CALLER's tenant and serves
/// only that tenant's totals.
///
/// This is the hostile A/B proof the retirement owes, on a real HOSTED GatewayHost with TWO fully enrolled
/// tenants and one unbound device:
///   1. SERVE - the data route answers 200 (not the old 404 refusal) for an enrolled tenant.
///   2. FAIL CLOSED - the SAME route answers 403 for a device whose key resolves to NO tenant, NEVER the
///      Local partition. A request that cannot be attributed to a tenant is refused, not served a wrong one.
///   3. ISOLATED - turns fed for tenant A are visible to A's read and INVISIBLE to B's read of the same feed.
///      One account's repos, agents, models and token spend never reach another's.
///
/// Self-host is unchanged and is the control in <see cref="HostedStatsSelfHostControlTests"/> below.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedStatsServeTests : IAsyncLifetime
{
    private const string Token = "test-token-stats-serve";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-stats-serve-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;        // fully enrolled tenant A
    private HttpClient _httpB = null!;        // fully enrolled tenant B
    private HttpClient _httpUnbound = null!;  // enrolled device, NO tenant binding
    private TenantId _tenantA;

    public HostedStatsServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-stats-serve-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com", out _tenantA);
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com", out _);

        // An enrolled device key bound to NO account: the strongest UNRESOLVED caller. It must be refused,
        // never served the Local partition. MTR-14B: an unbound device on hosted is an invalid credential
        // (invalidHostedBinding -> Revoked), so it is refused at the auth gate with 401 before reaching the
        // stats route's tenant-boundary 403. Isolation unchanged (no bound tenant -> no access); only the
        // denial layer moved.
        var unboundKey = _gateway.Devices.Register("dev-unbound", "MA").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
    }

    private HttpClient Enrolled(string deviceId, string subject, string email, out TenantId tenant)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var minted = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, minted.Value);
        tenant = minted;
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        _httpUnbound.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// SERVE: the data route answers 200 for an enrolled tenant on hosted - NOT the 404 refusal the deny used
    /// to give, and NOT the refusal envelope hiding behind a 200.
    /// </summary>
    [Fact]
    public async Task The_stats_feed_serves_an_enrolled_tenant_on_hosted()
    {
        var resp = await _httpA.GetAsync("stats/data");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not available on the hosted gateway", text, StringComparison.Ordinal);
    }

    /// <summary>The standalone dashboard page is retired (issue #587): /stats answers a redirect to the
    /// Cockpit /your-throttle route on hosted too. It carries no per-tenant data; the feed it used to
    /// fetch is what is tenant-gated.</summary>
    [Fact]
    public async Task The_stats_page_redirects_to_your_throttle_on_hosted()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var raw = new HttpClient(handler) { BaseAddress = _httpA.BaseAddress };
        raw.DefaultRequestHeaders.Authorization = _httpA.DefaultRequestHeaders.Authorization;
        var resp = await raw.GetAsync("stats");
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal("/your-throttle", resp.Headers.Location!.ToString());
    }

    /// <summary>
    /// FAIL CLOSED: the data route answers 403 for a device whose key resolves to NO tenant. Never a 200 with
    /// the Local partition's data, and never the old 404 - a bound-but-unattributable caller is refused with
    /// the tenant-required 403, which is the whole reason the feed is safe to serve on shared infrastructure.
    /// </summary>
    [Fact]
    public async Task The_stats_feed_refuses_an_unresolved_tenant_with_401()
    {
        var resp = await _httpUnbound.GetAsync("stats/data");
        // MTR-14B: unbound-on-hosted denied at the auth gate (401), before the route's tenant-boundary 403.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"turns\"", text, StringComparison.Ordinal);
    }

    /// <summary>Control: with no key the host-wide auth gate still refuses first, so the retirement did not
    /// open the route to anonymous callers.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var noAuth = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("stats/data")).StatusCode);
    }

    /// <summary>
    /// ISOLATED: turns fed into the aggregator for tenant A are counted in A's feed and are INVISIBLE to B.
    /// Feeds A a distinctive voice/phone bucket; A's /stats/data reports it, B's reports an empty feed. Proves
    /// the un-denied read is scoped to the caller's tenant, not fleet-global.
    /// </summary>
    [Fact]
    public async Task One_tenants_turns_are_invisible_to_another_tenant_on_hosted()
    {
        _gateway.InputStats!.Observe(new SessionDto
        {
            SessionId = "s-alpha",
            RepoPath = @"D:\ReposFred\alpha-only-repo",
            InputStats = new InputStatsDto
            {
                Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = 7, Characters = 700 } },
            },
        }, null, _tenantA);

        // Tenant A sees its own turns.
        var a = await ReadFeed(_httpA);
        var aBucket = Assert.Single(a.GetProperty("buckets").EnumerateArray());
        Assert.Equal("voice", aBucket.GetProperty("modality").GetString());
        Assert.Equal(7, aBucket.GetProperty("turns").GetInt64());
        // And its repo tally carries A's repo.
        Assert.Contains(a.GetProperty("repos").EnumerateArray(),
            r => r.GetProperty("repoName").GetString() == "alpha-only-repo");

        // Tenant B sees NONE of it - an empty feed, not A's totals.
        var b = await ReadFeed(_httpB);
        Assert.Empty(b.GetProperty("buckets").EnumerateArray());
        Assert.Empty(b.GetProperty("repos").EnumerateArray());
    }

    private static async Task<JsonElement> ReadFeed(HttpClient http)
    {
        var resp = await http.GetAsync("stats/data");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// Boots ONLY the stats group on an ephemeral port, exactly as <see cref="StatsPageEndpointTests"/> does, and
/// hands the caller the route group back so a test can map routes onto it. Used by the self-host control
/// below; on self-host the data route resolves to the single Local tenant (a null tenant boundary), so it
/// serves its real payload.
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
}

/// <summary>
/// THE SELF-HOST CONTROL, STATED EXPLICITLY.
///
/// Self-host is the control for this entire hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. <see cref="StatsPageEndpointTests"/> does not prove it: those tests never mention
/// <c>CC_GATEWAY_HOSTED</c> and pass only because the runner happens to leave it unset. If that ambient
/// default ever flipped - one leaked environment variable, one continuous-integration image change, one test
/// that forgot to restore it - they would keep passing while self-host was broken, because they assert
/// nothing about which mode they are in.
///
/// So this class sets the variable itself, to BOTH non-hosted values that occur in practice: absent, and
/// present-but-not-"1". It then asserts the routes serve their REAL PAYLOADS - the seeded counts, the ranked
/// repository, the honesty caveats, the dashboard markup - and not merely that a refusal string is absent.
/// On self-host the data route resolves to the single Local tenant, so it serves exactly as it always has.
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

    /// <summary>The standalone dashboard page is retired (issue #587): on self-host too, /stats answers a
    /// redirect to the Cockpit /your-throttle route rather than serving embedded HTML.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_stats_page_redirects_to_your_throttle_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await StatsGroupProbeHost.StartAsync(SeededAggregator());
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var raw = new HttpClient(handler) { BaseAddress = http.BaseAddress };
            var resp = await raw.GetAsync("/stats");
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal("/your-throttle", resp.Headers.Location!.ToString());
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

    /// <summary>A route added to the group still serves on self-host, in both non-hosted forms.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await StatsGroupProbeHost.StartAsync(
            SeededAggregator(),
            mapIntoGroup: group => group.MapGet("/stats/added-later",
                () => Results.Json(new { probe = "served" })));
        try
        {
            var resp = await http.GetAsync("/stats/added-later");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("served", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
