using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Cockpit;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Epic #967 / issue #969: the React desktop Cockpit is served by the REAL Gateway host at /c,
/// side by side with the fallback Blazor Cockpit. These tests start an actual <see cref="GatewayHost"/>
/// (the same wiring the product runs) with a dead Blazor Cockpit proxy port, so an unclaimed path
/// answers the "Cockpit starting" interstitial - which is the observable proof of coexistence: /c is
/// owned by the React app while everything else still falls through to Blazor.
///
/// The built React app normally lands in <c>wwwroot/c</c> via a release-gated MSBuild target; these
/// tests fabricate a tiny shell there (index.html + one hashed asset) so the serving behavior can be
/// proven without running the front-end build. The directory is created under the test's own base
/// directory and removed on teardown.
/// </summary>
public sealed class CockpitReactAppServingTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private const string ShellMarker = "<div id=\"root\"></div><!-- cockpit-react-shell -->";
    private const string AssetBody = "export const cockpit = 1;";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Fabricate the built shell the release pipeline would stage into wwwroot/c.
        var webRoot = CockpitReactApp.WebRoot;
        if (Directory.Exists(webRoot)) Directory.Delete(webRoot, true);
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><head><script type=\"module\" src=\"/c/assets/index-abc123.js\"></script></head>"
            + "<body>" + ShellMarker + "</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "index-abc123.js"), AssetBody);

        // Dead Blazor Cockpit proxy port (1): a path the React app does not claim answers the 503
        // interstitial, which is the observable proof it fell through to the Blazor fallback.
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(CockpitReactApp.WebRoot)) Directory.Delete(CockpitReactApp.WebRoot, true); }
        catch { }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task Root_of_c_serves_the_react_shell()
    {
        var resp = await _http.GetAsync("c");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deep_client_route_falls_back_to_the_shell_for_the_router()
    {
        // A hard navigation to a client-side route (no matching file) must serve index.html so the
        // React router can resolve it - the single-page-app fallback.
        var resp = await _http.GetAsync("c/fleet");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hashed_asset_is_served_immutably()
    {
        var resp = await _http.GetAsync("c/assets/index-abc123.js");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(AssetBody, await resp.Content.ReadAsStringAsync());
        Assert.Contains("immutable", resp.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Unknown_path_still_falls_through_to_the_blazor_cockpit()
    {
        // Coexistence: a path the React app does not own is NOT served the shell; it flows to the
        // fallback proxy (dead port -> interstitial). This is the side-by-side guarantee - Blazor
        // keeps serving every path /c does not claim.
        var resp = await _http.GetAsync("some-blazor-page");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("Cockpit starting", await resp.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
