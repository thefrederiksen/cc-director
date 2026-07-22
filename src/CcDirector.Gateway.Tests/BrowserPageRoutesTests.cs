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
/// The observable proof a browser navigation took the SHELL path (not the JSON endpoint) is that the
/// response is NOT application/json. Its exact shape depends on whether the React bundle is staged into
/// this host (wwwroot/c, release-gated): with the bundle present the shell answers 200 text/html; with
/// it absent the shell answers 404 text/plain "React Cockpit not built". A test host built in Release
/// (or with BuildCockpit=true) HAS the bundle; a routine Debug host does not - so these tests key the
/// shell-path assertion on <see cref="CockpitReactApp.WebRoot"/> being present (the same state the
/// production router reads) instead of hard-coding one environment's answer. This makes the test
/// deterministic on any machine/config while still proving the real routing fork (issue #1055).
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
    [InlineData("GET", "/fleet", "text/html")]                 // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/schedule", "text/html")]              // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/dictionary", "text/html")]            // no same-path JSON: served by SPA fallback
    [InlineData("GET", "/transcripts", "text/html")]           // no same-path JSON: served by SPA fallback
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

        // The navigation took the React-shell path, not the JSON endpoint (which answers 200
        // application/json). The shell's exact response depends on whether the bundle is staged here.
        await AssertServedCockpitShellAsync(resp);
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

        // The navigation took the React-shell path, not the API (which would answer JSON).
        await AssertServedCockpitShellAsync(resp);
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

    /// <summary>
    /// Assert a browser navigation to a dual-use path was served the React SHELL, not the JSON API
    /// endpoint - deterministically, regardless of whether this host has the React bundle staged.
    /// The environment-independent invariant is "never application/json"; the concrete shape is keyed on
    /// the same <see cref="CockpitReactApp.WebRoot"/> presence the production router reads, so the test
    /// passes on a Debug host (bundle absent -> 404 "not built") and a Release host (bundle present ->
    /// 200 text/html shell) alike (issue #1055).
    /// </summary>
    private static async Task AssertServedCockpitShellAsync(HttpResponseMessage resp)
    {
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        var body = await resp.Content.ReadAsStringAsync();

        // Invariant in every environment: a browser navigation is NEVER served the JSON endpoint.
        Assert.NotEqual("application/json", mediaType);

        var bundlePresent = Directory.Exists(CockpitReactApp.WebRoot)
                            && File.Exists(Path.Combine(CockpitReactApp.WebRoot, "index.html"));
        if (bundlePresent)
        {
            // Release host: the shell document is served.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html", mediaType);
        }
        else
        {
            // Debug host: the shell path answers the "not built" notice - still NOT the JSON endpoint.
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("text/plain", mediaType);
            Assert.Contains("React Cockpit not built", body);
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
