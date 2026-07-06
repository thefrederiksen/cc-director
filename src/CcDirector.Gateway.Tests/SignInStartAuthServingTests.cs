using System.Net;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Epic #1069, issue #1076: with the enforced Gateway auth gate ON (#917), the credential-free cloud
/// sign-in START front door <c>/account/sign-in-start</c> must be reachable to a SIGNED-OUT browser (no
/// <c>cc-gateway-token</c> cookie, no Bearer) - served, not 401 and not bounced to the raw-token wall
/// <c>/login</c> - while every <c>/account/*</c> DATA endpoint and every other data route stay gated.
/// This boots a real <see cref="GatewayHost"/> with auth ON and proves both halves without weakening the
/// gate. The GET front door is side-effect-free (it never opens a browser), so booting the real host is safe.
/// </summary>
public sealed class SignInStartAuthServingTests : IAsyncLifetime
{
    private const string Token = "test-token-1076";
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
    public async Task SignInStart_browser_navigation_without_a_credential_is_served_not_redirected_to_login()
    {
        // A signed-out person opening the sign-in front door announces Accept: text/html and carries no
        // cookie/Bearer. The gate must serve it (200), NOT redirect to /login and NOT answer 401.
        using var req = new HttpRequestMessage(HttpMethod.Get, AccountSignInStartEndpoint.Path);
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("Sign in with DevThrottle", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInCallback_without_a_gateway_token_is_served_not_redirected_to_login()
    {
        // Issue #1080: the reachable front-door callback the cloud sign-in page redirects the user's own
        // browser back to must be public - the browser completing sign-in has no Gateway token yet. With no
        // credential in the query it renders a "did not complete" status page (200), never a 401 and never a
        // bounce to the raw-token wall /login. (No token is supplied, so nothing is stored.)
        using var req = new HttpRequestMessage(HttpMethod.Get, AccountSignInCallbackEndpoint.Path);
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AccountStatus_without_a_credential_still_401s()
    {
        // The public allow-list is exact-match: only /account/sign-in-start opened. The sibling
        // /account/status DATA endpoint must still be gated (JSON 401 for a program request).
        using var res = await _http.GetAsync("/account/status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AccountStatus_browser_navigation_without_a_credential_is_redirected_to_login()
    {
        // A browser request (Accept: text/html) to the gated /account/status is bounced to the raw-token
        // wall exactly as before - proof the front door did not accidentally open the /account data surface.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/account/status");
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        Assert.StartsWith("/login", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Representative_data_route_without_a_credential_still_401s()
    {
        // A representative data route (/sessions) stays Bearer-gated; the front door weakened nothing.
        using var res = await _http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Healthz_stays_public()
    {
        using var res = await _http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
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
