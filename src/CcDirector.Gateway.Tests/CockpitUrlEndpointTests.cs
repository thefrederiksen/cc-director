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
/// <c>GET /cockpit</c> and the <c>CockpitUrl</c> on <c>GET /gateway/about</c> both hand back
/// <c>{base}/cockpit</c> in hosted mode, where base comes from <c>CC_GATEWAY_PUBLIC_URL</c>. These are the
/// tests that go red if either call site is reverted to <c>TailscaleIdentity.TryGetFrontDoorBaseUrl()</c>
/// (which, with no tailnet in the test host, yields null where the public URL should be) - a green
/// pure-resolver test cannot catch a mis-wired call site. These two are the surfaces a hosted client actually
/// reads for the public URL.
///
/// <c>GET /gateway/settings</c> once carried a third <c>cockpit.url</c> copy, but that route is part of the
/// owner-settings group DENIED on the hosted Gateway (issue #1863), so on hosted it serves the refusal and no
/// cockpit URL at all - proved by <see cref="Hosted_settings_route_is_denied_and_carries_no_cockpit_url"/>.
/// That copy is unused anyway: boot, navigation, and URL discovery do not consume it; its only reader is the
/// settings-page load, which renders a load-error state on the deny.
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
    public async Task Hosted_settings_route_serves_per_account_and_carries_no_cockpit_url()
    {
        await WithHostedGateway(PublicBase, async (http, gateway) =>
        {
            // Issue #2022 part 2: /gateway/settings is now per-account and SERVES on hosted (the deny retired).
            // It carries only account settings - never the cockpit.url it once did. The live public-URL surfaces
            // are GET /cockpit and GET /gateway/about, asserted above.
            //
            // An UNBOUND device resolves to no tenant, so it is refused with 403 - never the Local partition,
            // never a cockpit url.
            var unboundKey = gateway.Devices.Register("cockpiturl-unbound", "PHONE").DeviceKey;
            using (var req = new HttpRequestMessage(HttpMethod.Get, "gateway/settings"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
                using var resp = await http.SendAsync(req);
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }

            // An ENROLLED tenant is served the per-account snapshot, which carries NO cockpit block/url.
            var boundKey = gateway.Devices.Register("cockpiturl-bound", "PHONE").DeviceKey;
            var tenant = gateway.TenantRegistry.MintOrLookupBySubject("sub-cockpiturl", "cockpiturl@example.com");
            gateway.Devices.SetAccountBinding("cockpiturl-bound", "sub-cockpiturl", tenant.Value);
            using (var req = new HttpRequestMessage(HttpMethod.Get, "gateway/settings"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", boundKey);
                using var resp = await http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                Assert.DoesNotContain("cockpit", body, StringComparison.OrdinalIgnoreCase);
            }
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
