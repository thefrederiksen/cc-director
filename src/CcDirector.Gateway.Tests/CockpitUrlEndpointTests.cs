using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Endpoint-level proof that the P1 fix is WIRED IN, not just present as a helper: the live Gateway
/// <c>GET /cockpit</c> and the <c>CockpitUrl</c> on <c>GET /gateway/about</c> hand back the configured
/// public cockpit URL in hosted mode. These are the tests that go red if either call site is reverted to
/// <c>TailscaleIdentity.TryGetFrontDoorBaseUrl()</c> (which, with no tailnet in the test host, yields null
/// where the public URL should be) - a green pure-resolver test cannot catch that.
///
/// Only the HOSTED direction is asserted at the endpoint: it is deterministic (no tailscale involved). The
/// self-host direction depends on whether a tailnet exists on the build host, so its byte-identical proof
/// lives in <see cref="GatewayCockpitUrlTests"/> against the pure resolver instead.
///
/// Env vars are process-global; saved and restored in a finally, matching the established pattern in
/// <c>HealthzTenantLeakTests</c> / <c>HostedStatsDenyTests</c> for the hosted-mode Gateway tests.
/// </summary>
public sealed class CockpitUrlEndpointTests
{
    private const string Token = "test-token";
    private const string PublicCockpit = "https://cockpit.devthrottle.com";

    [Fact]
    public async Task Hosted_cockpit_returns_configured_public_url()
    {
        await WithHostedGateway(PublicCockpit, async (http, _) =>
        {
            var info = await GetJson<CockpitInfoDto>(http, "cockpit", auth: false);

            // The client is dumb - it opens exactly this. Hosted must hand it the public cockpit URL,
            // never the (absent) tailnet front door. Trailing slash is the historic call-site shape.
            Assert.Equal(PublicCockpit + "/", info.Url);
            Assert.True(info.Up);
        });
    }

    [Fact]
    public async Task Hosted_about_CockpitUrl_returns_configured_public_url()
    {
        await WithHostedGateway(PublicCockpit, async (http, _) =>
        {
            // /gateway/about is credential-gated; the shared machine token is a valid Bearer.
            var about = await GetJson<AboutDto>(http, "gateway/about", auth: true);

            Assert.Equal(PublicCockpit + "/", about.CockpitUrl);
        });
    }

    [Fact]
    public async Task Hosted_cockpit_without_configured_url_fails_loud()
    {
        // NO fallback end-to-end: hosted with the public URL unset is a deploy misconfiguration, so the
        // endpoint throws (500) rather than serving a null the client would misread as "Tailscale down".
        await WithHostedGateway(configuredCockpitUrl: null, async (http, _) =>
        {
            var resp = await http.GetAsync("cockpit");

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        });
    }

    // ---- harness (mirrors HealthzTenantLeakTests) -------------------------------------------------

    private static async Task WithHostedGateway(
        string? configuredCockpitUrl, Func<HttpClient, GatewayHost, Task> body)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorUrl = Environment.GetEnvironmentVariable(GatewayCockpitUrl.PublicCockpitUrlEnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable(GatewayCockpitUrl.PublicCockpitUrlEnvVar, configuredCockpitUrl);

        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-cockpiturl-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        try
        {
            await gateway.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            await body(http, gateway);
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable(GatewayCockpitUrl.PublicCockpitUrlEnvVar, priorUrl);
            // Deliberately NOT deleting instancesDir - see the note in HealthzTenantLeakTests: deleting it
            // can raise a FileSystemWatcher event on a pool thread after teardown and abort the whole run.
        }
    }

    private static async Task<T> GetJson<T>(HttpClient http, string path, bool auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (auth)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"{path} returned no body");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
