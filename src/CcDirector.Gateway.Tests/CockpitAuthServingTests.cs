using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #920 (security epic #916, Phase 2): the Cockpit must authenticate under the enforced Gateway
/// auth gate (#917). /cockpit is a dual-use path - the JSON API form stays public so the desktop app's
/// Open Cockpit / Learn buttons can resolve the front-door URL with no credential, but a BROWSER
/// navigation to /cockpit (Accept: text/html) is the Blazor shell whose /_blazor circuit and /_framework
/// assets are gated. Serving that shell unauthenticated produced a dead Cockpit whose circuit 401s, so a
/// browser navigation to /cockpit is now driven to /login first; after sign-in the cookie carries to the
/// shell's assets so they authenticate. This boots a Gateway with auth ON and proves each half without
/// weakening the gate on /_blazor or session data.
/// </summary>
public sealed class CockpitAuthServingTests : IAsyncLifetime
{
    private const string Token = "test-token-920";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // cockpitProxyPort: 1 is a dead loopback port - a request that PASSES the gate and is forwarded
        // to the Cockpit hits the "Cockpit starting..." interstitial (503), never a real dev Cockpit.
        // That 503 is itself proof the gate accepted the request and handed it to the proxy.
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Cockpit_browser_navigation_without_cookie_is_redirected_to_login()
    {
        // A person opening the Cockpit in a browser announces Accept: text/html. With no cc-gateway-token
        // cookie the gate must drive them to /login rather than hand back a dead shell.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/cockpit");
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        Assert.StartsWith("/login", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Cockpit_json_api_form_stays_public_for_programs()
    {
        // The desktop app's Open Cockpit / Learn buttons GET /cockpit as JSON with no credential and no
        // Accept: text/html. That program call must still succeed (200) - the native app is out of scope
        // for #920 and confirmed passing by the Phase 0 scan.
        using var res = await _http.GetAsync("/cockpit");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Cockpit_browser_navigation_with_the_cookie_passes_the_gate()
    {
        // With the cc-gateway-token cookie present (the state after sign-in), a browser navigation to
        // /cockpit must NOT be redirected to /login and must NOT be 401 - it passes the gate and is
        // forwarded to the Cockpit (here the dead-port interstitial, 503). Either way it cleared auth.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/cockpit");
        req.Headers.Accept.ParseAdd("text/html");
        req.Headers.Add("Cookie", $"{Util.AuthMiddleware.CookieName}={Token}");
        using var res = await _http.SendAsync(req);

        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Blazor_circuit_without_a_credential_still_401s()
    {
        // The gate must NOT be weakened: the /_blazor SignalR circuit (session-data plumbing) still
        // requires the credential. A fetch/WebSocket negotiate carries no Accept: text/html, so an
        // unauthenticated call gets the JSON 401, never a redirect and never a pass-through.
        using var res = await _http.GetAsync("/_blazor");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Framework_assets_without_a_credential_still_401()
    {
        // /_framework assets are gated too (this fix does not classify them public); the cookie present
        // after sign-in is what lets the browser load them. Unauthenticated -> 401.
        using var res = await _http.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Regression_sessions_requires_bearer_and_healthz_stays_public()
    {
        // Phase 0 PASS surfaces must be unchanged by this fix: /sessions is Bearer-gated, /healthz public.
        using var noBearer = await _http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);

        using var withBearer = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        withBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var okSessions = await _http.SendAsync(withBearer);
        Assert.Equal(HttpStatusCode.OK, okSessions.StatusCode);

        using var health = await _http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
