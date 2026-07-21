using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #806 (mobile foundation), AC5: the mobile app renders with global Gateway auth either
/// on or off. With auth ON, the app shell at /mobile loads WITHOUT a credential (it carries the
/// injected token, not a secret), while the data endpoint /sessions stays Bearer-gated - so the
/// injected token is exactly what makes the roster load. This boots a Gateway with auth ON and
/// proves both halves.
///
/// It also covers the /m -> /mobile re-base (Phase D): the canonical /mobile shell is the public,
/// non-auth-gated surface, and the legacy /m mount 301-redirects to /mobile (with the sub-path and
/// query string preserved) while STAYING public - so an installed phone PWA on the old path is
/// redirected, never auth-walled.
/// </summary>
public sealed class MobileAuthServingTests : IAsyncLifetime
{
    private const string Token = "test-token-806";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
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
    public async Task Sessions_requires_bearer_when_auth_is_on()
    {
        using var res = await _http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Sessions_returns_200_with_the_injected_bearer()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Mobile_shell_is_public_and_not_login_gated_when_auth_is_on()
    {
        // The /mobile shell is exempt from the global gate, so it reaches the mobile handler instead of
        // being 302-redirected to /login. Whether the mobile app is staged into wwwroot/mobile depends on
        // the build: a bare test build serves nothing there (404), while a build that staged the app
        // serves the shell (200). EITHER outcome proves the request was NOT auth-gated; the states
        // this rules out are a redirect to /login or a 401. (Asserting 404 specifically was brittle -
        // it broke once CI builds began staging the app, issue #818.)
        using var res = await _http.GetAsync("/mobile");
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.True(
            res.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"/mobile must be served (200) or absent (404), never auth-gated; got {(int)res.StatusCode} {res.StatusCode}");
    }

    [Fact]
    public async Task Legacy_m_301_redirects_to_mobile_and_is_not_auth_gated()
    {
        // The legacy /m mount is public (so the redirect is reachable before any credential) AND answers
        // a 301 to /mobile - never a 302 to /login and never a 401. This is what keeps an installed phone
        // PWA / bookmark on /m working after the re-base.
        using var res = await _http.GetAsync("/m");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("/mobile/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Legacy_m_subpath_301_preserves_the_path_and_query()
    {
        // A deep link on the old mount (the sign-in callback devthrottle.com still hands back to, plus any
        // bookmarked route) redirects to the same route under /mobile, carrying the query string. The URL
        // fragment (the device key / access token) is re-attached by the browser across the 301, which no
        // server test can observe - it never reaches the server - so this asserts the server half: path +
        // query preserved, Location carries no fragment of its own.
        using var res = await _http.GetAsync("/m/device-callback?state=abc");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("/mobile/device-callback?state=abc", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Legacy_m_301_location_carries_no_fragment_so_the_signin_token_survives()
    {
        // The sign-in token rides the URL FRAGMENT (#access_token=... / #device_key=...), which never
        // reaches the server. A browser preserves that fragment across a redirect ONLY when the redirect's
        // Location has no fragment of its own - then it re-attaches the original. So the server-side
        // guarantee that makes the token survive is: the 301 Location must NEVER contain a '#'. If this
        // handler ever appended a fragment to Location, the browser would use THAT and drop the token.
        // Emitting a fragment on the redirect target reddens this (fails-on-purpose proof).
        foreach (var path in new[] { "/m", "/m/device-callback", "/m/device-callback?state=abc", "/m/session/s-1" })
        {
            using var res = await _http.GetAsync(path);
            Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
            var location = res.Headers.Location?.OriginalString ?? "";
            Assert.StartsWith("/mobile", location);
            Assert.DoesNotContain("#", location);
        }
    }

    [Fact]
    public async Task Both_enroll_mounts_are_public_reaching_the_endpoint_not_the_gate()
    {
        // POST /mobile/enroll is the canonical mint seam and POST /m/enroll is its back-compat alias; both
        // are exempt from the global gate so a credential-less device reaches the enrollment endpoint (which
        // carries its own account-scoped authorization) rather than being 401'd or redirected to /login. An
        // empty body makes the endpoint answer a 4xx/5xx of its own - the point here is only that the gate
        // did NOT reject the request, i.e. it is never 401 Unauthorized and never a /login redirect.
        foreach (var path in new[] { "/mobile/enroll", "/m/enroll" })
        {
            using var res = await _http.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
            Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        }
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
