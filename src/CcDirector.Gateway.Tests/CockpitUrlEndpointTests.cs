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
/// <c>GET /cockpit</c>, the <c>CockpitUrl</c> on <c>GET /gateway/about</c>, and the <c>cockpit.url</c> on
/// <c>GET /gateway/settings</c> all hand back <c>{base}/cockpit</c> in hosted mode, where base comes from
/// <c>CC_GATEWAY_PUBLIC_URL</c>. These are the tests that go red if any of the THREE call sites is reverted
/// to <c>TailscaleIdentity.TryGetFrontDoorBaseUrl()</c> (which, with no tailnet in the test host, yields
/// null where the public URL should be) - a green pure-resolver test cannot catch a mis-wired call site.
///
/// Only the HOSTED direction is asserted at the endpoint: it is deterministic (no tailscale involved). The
/// self-host direction depends on whether a tailnet exists on the build host, so its byte-identical proof
/// lives in <see cref="GatewayPublicUrlTests"/> against the pure resolver instead.
///
/// Env vars are process-global; saved and restored in a finally, matching the established pattern in
/// <c>HealthzTenantLeakTests</c> / <c>HostedStatsDenyTests</c> for the hosted-mode Gateway tests.
/// </summary>
public sealed class CockpitUrlEndpointTests
{
    private const string Token = "test-token";

    // A deliberately NON-production, clearly-fake base. The endpoint must hand back values DERIVED from
    // this configured base, so replacing ResolveCockpit() with a hardcoded production constant
    // (https://gateway.devthrottle.com/cockpit) reddens these tests instead of coincidentally matching.
    private const string PublicBase = "https://gw.test.invalid";
    private const string ExpectedCockpit = PublicBase + "/cockpit";

    [Fact]
    public async Task Hosted_cockpit_returns_configured_public_cockpit_url()
    {
        await WithHostedGateway(PublicBase, async (http, _) =>
        {
            var info = await GetJson<CockpitInfoDto>(http, "cockpit", auth: false);

            // The client is dumb - it opens exactly this. Hosted must hand it {base}/cockpit, never the
            // (absent) tailnet front door.
            Assert.Equal(ExpectedCockpit, info.Url);
            Assert.True(info.Up);
        });
    }

    [Fact]
    public async Task Hosted_about_CockpitUrl_returns_configured_public_cockpit_url()
    {
        await WithHostedGateway(PublicBase, async (http, gateway) =>
        {
            // /gateway/about is credential-gated. On hosted the shared machine token is NO LONGER a valid
            // Bearer (production-readiness MH-2 - it authenticates with no tenant), so the credential is a
            // per-device key issued at enrollment, exactly as a real hosted caller uses.
            var deviceKey = gateway.Devices.Register("cockpiturl-about", "PHONE").DeviceKey;
            var about = await GetJson<AboutDto>(http, "gateway/about", bearer: deviceKey);

            Assert.Equal(ExpectedCockpit, about.CockpitUrl);
        });
    }

    [Fact]
    public async Task Hosted_settings_cockpit_url_returns_configured_public_cockpit_url()
    {
        await WithHostedGateway(PublicBase, async (http, gateway) =>
        {
            // The third call site (first cut missed it): /gateway/settings.cockpit.url must hand back the
            // SAME {base}/cockpit, not the raw front-door root it emitted before. Reverting this call site
            // to TryGetFrontDoorBaseUrl() turns this red (null in the test host).
            //
            // MH-2: authenticate with a per-device key, not the shared token (rejected on hosted).
            var deviceKey = gateway.Devices.Register("cockpiturl-settings", "PHONE").DeviceKey;
            using var req = new HttpRequestMessage(HttpMethod.Get, "gateway/settings");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var url = doc.RootElement.GetProperty("cockpit").GetProperty("url").GetString();
            Assert.Equal(ExpectedCockpit, url);
        });
    }

    [Fact]
    public async Task Hosted_cockpit_without_configured_base_fails_loud()
    {
        // NO fallback end-to-end: hosted with the public base unset is a deploy misconfiguration, so the
        // endpoint throws (500) rather than serving a null the client would misread as "Tailscale down".
        await WithHostedGateway(configuredBase: null, async (http, _) =>
        {
            var resp = await http.GetAsync("cockpit");

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        });
    }

    // ---- harness (mirrors HealthzTenantLeakTests) -------------------------------------------------

    private static async Task WithHostedGateway(
        string? configuredBase, Func<HttpClient, GatewayHost, Task> body)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorUrl = Environment.GetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, configuredBase);

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
            Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, priorUrl);
            // Deliberately NOT deleting instancesDir - see the note in HealthzTenantLeakTests: deleting it
            // can raise a FileSystemWatcher event on a pool thread after teardown and abort the whole run.
        }
    }

    private static async Task<T> GetJson<T>(HttpClient http, string path, bool auth = false, string? bearer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        else if (auth)
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
