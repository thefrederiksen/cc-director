using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Cockpit;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Browser-aware front door (the Cockpit sitemap): GET /sessions, /directors, and /cockpit
/// are BOTH an API endpoint (JSON) and a Cockpit page (HTML). A browser navigation announces
/// itself with "Accept: text/html", which no API client sends, so the Gateway serves those
/// navigations the React Cockpit shell (issue #979) and serves JSON to everything else.
///
/// In a Debug test build the React bundle is not built into the host (wwwroot/c is release-gated),
/// so the shell path answers 404 with the "not built" notice. That 404 is still the observable proof
/// the request took the shell path rather than the JSON endpoint (which answers 200 application/json).
/// </summary>
public sealed class BrowserPageRoutesTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---- policy unit tests ----

    [Theory]
    [InlineData("/sessions")]
    [InlineData("/directors")]
    [InlineData("/cockpit")]
    [InlineData("/lists")]                        // #979 regression: Work-List API vs Lists page
    [InlineData("/sessions/")]                    // trailing slash
    [InlineData("/SESSIONS")]                     // case-insensitive
    [InlineData("/LISTS")]                         // case-insensitive
    [InlineData("/sessions/abc123")]              // detail page: one id segment
    [InlineData("/directors/abc123")]             // detail page: one id segment
    [InlineData("/cockpit/abc123")]               // deep-linked cockpit session
    public void Browser_navigation_on_dual_use_path_is_a_page_request(string path)
    {
        Assert.True(CockpitReactApp.IsBrowserPageRequest(
            "GET", path, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"));
    }

    [Theory]
    [InlineData("GET", "/sessions", "application/json")]       // API client
    [InlineData("GET", "/sessions", "*/*")]                    // curl / fetch default
    [InlineData("GET", "/sessions", null)]                     // no Accept at all
    [InlineData("POST", "/sessions", "text/html")]             // wrong method
    [InlineData("GET", "/sessions/abc123", "application/json")] // detail JSON stays API
    [InlineData("GET", "/healthz", "text/html")]               // not a dual-use path
    [InlineData("GET", "/lists", "application/json")]           // Work-List API client stays JSON
    [InlineData("GET", "/lists", null)]                        // curl / script default stays JSON
    [InlineData("GET", "/sessions/abc/turnbriefs", "text/html")] // 3 segments = API only
    [InlineData("GET", "/directors/abc/repos", "text/html")]     // 3 segments = API only
    // Audited NON-collisions: a React page whose OWN path has no top-level single-segment JSON
    // endpoint must NOT be a page-request root, so its nested JSON path is never shadowed for a
    // browser. These would break if someone over-broadly added the page root to BrowserPageRoots.
    [InlineData("GET", "/exes/list", "text/html")]             // page is /exes; /exes/list stays JSON
    [InlineData("GET", "/account/status", "text/html")]        // page is /account; nested API stays JSON
    [InlineData("GET", "/account/devices", "text/html")]       // page is /account; nested API stays JSON
    [InlineData("GET", "/wingman/queue", "text/html")]         // page is /wingman; queue API stays JSON
    [InlineData("GET", "/fleet", "text/html")]                 // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/schedule", "text/html")]              // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/dictionary", "text/html")]            // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/transcripts", "text/html")]           // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/telemetry", "text/html")]             // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/about", "text/html")]                 // no same-path JSON: served by SPA fallback
    public void Non_navigation_requests_are_not_page_requests(string method, string path, string? accept)
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest(method, path, accept));
    }

    // ---- wire tests ----

    [Theory]
    [InlineData("sessions")]
    [InlineData("directors")]
    [InlineData("cockpit")]
    [InlineData("lists")]
    public async Task Browser_navigation_is_served_the_cockpit_shell(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var resp = await _http.SendAsync(req);

        // The navigation took the React-shell path, not the JSON endpoint - the response is NEVER
        // application/json. Whether the shell is actually built depends on the host: an unbuilt Debug host
        // answers 404 with the "React Cockpit not built" text/plain notice; a host where the shell WAS
        // built (CI builds wwwroot/c before the tests) serves the shell index (200 text/html). Assert the
        // invariant that holds either way (issue #1048 follow-up: the old assertion assumed the shell was
        // never built and broke when CI started building it).
        Assert.NotEqual("application/json", resp.Content.Headers.ContentType?.MediaType);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            Assert.Contains("React Cockpit not built", await resp.Content.ReadAsStringAsync());
        else
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("directors")]
    [InlineData("cockpit")]
    [InlineData("lists")]
    public async Task Api_clients_keep_getting_json(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        // No Accept header at all - the way HttpClient/curl/scripts call the API.

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Session_detail_navigation_is_served_the_cockpit_shell()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "sessions/00000000-0000-0000-0000-000000000000");
        req.Headers.Accept.ParseAdd("text/html");

        var resp = await _http.SendAsync(req);

        // The navigation took the React-shell path, not the API (which would answer JSON). Whether the
        // shell is built depends on the host (see Browser_navigation_is_served_the_cockpit_shell): assert
        // the build-state-independent invariant - it is never JSON, and if unbuilt it is the notice.
        Assert.NotEqual("application/json", resp.Content.Headers.ContentType?.MediaType);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            Assert.Contains("React Cockpit not built", await resp.Content.ReadAsStringAsync());
        else
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Three_segment_api_subpath_is_untouched_even_for_browsers()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "sessions/00000000-0000-0000-0000-000000000000/turnbriefs");
        req.Headers.Accept.ParseAdd("text/html");

        var resp = await _http.SendAsync(req);

        // The turn-brief API endpoint answered (JSON), never the Cockpit interstitial.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
