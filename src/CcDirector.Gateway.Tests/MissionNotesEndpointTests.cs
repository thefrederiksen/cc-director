using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP tests for the mission-WHY surface (Mission Screen mission, Phase 1b, issue #1405) over a real
/// Gateway with auth ON. Two things are PROVEN here, not assumed:
///  1. AUTH: an unauthenticated GET and PUT of /gateway/missions/notes both return the JSON 401 - the
///     host-wide device-key middleware really does gate this new client route (the Architect's hard
///     requirement). A wrong assumption here would leave every mission's WHY world-readable/writable.
///  2. DURABLE + SHARED: a WHY set through one authenticated client is read back by a FRESH client, and
///     survives a Gateway restart (a second host over the same store file) - the mission's core promise
///     that the WHY is not per-browser.
/// Every test uses an isolated temp store path so it never touches the real mission-notes.json.
/// </summary>
public sealed class MissionNotesEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-1405";
    private const string Route = "/gateway/missions/notes";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-missionnotes-ep-" + Guid.NewGuid().ToString("N"));
    private string MissionNotesPath => Path.Combine(_dir, "mission-notes.json");
    private string InstancesDir => Path.Combine(_dir, "instances");

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: InstancesDir,
            workListsPath: Path.Combine(_dir, "worklists.json"),
            missionNotesPath: MissionNotesPath);
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
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- 1. AUTH PROOF (the Architect's hard requirement) ------------------------------------

    [Fact]
    public async Task Unauthenticated_GET_is_401()
    {
        using var res = await _http.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_PUT_is_401()
    {
        using var res = await _http.PutAsJsonAsync(Route, new { mission = "Car Mode", why = "sneaky" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        // And the write was refused, not silently applied: an authenticated read shows nothing.
        Assert.Null(await GetWhy("Car Mode"));
    }

    // ---- 2. AUTHENTICATED READ/SET ------------------------------------------------------------

    [Fact]
    public async Task Authenticated_GET_is_initially_empty()
    {
        using var res = await Authed(HttpMethod.Get, Route);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("notes").EnumerateArray());
    }

    [Fact]
    public async Task PUT_sets_a_why_and_GET_reads_it_back()
    {
        using var put = await AuthedJson(HttpMethod.Put, Route, new { mission = "Gateway Cleanup", why = "all traffic onto the tunnel" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var putDoc = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        var note = putDoc.RootElement.GetProperty("note");
        Assert.Equal("gateway cleanup", note.GetProperty("key").GetString());
        Assert.Equal("Gateway Cleanup", note.GetProperty("mission").GetString());
        Assert.Equal("all traffic onto the tunnel", note.GetProperty("why").GetString());

        Assert.Equal("all traffic onto the tunnel", await GetWhy("Gateway Cleanup"));
    }

    [Fact]
    public async Task A_blank_mission_is_400()
    {
        using var res = await AuthedJson(HttpMethod.Put, Route, new { mission = "   ", why = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PUT_with_an_empty_why_clears_the_note()
    {
        using (var set = await AuthedJson(HttpMethod.Put, Route, new { mission = "Snooze Length", why = "timed snooze that always comes back" }))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("timed snooze that always comes back", await GetWhy("Snooze Length"));

        using var clear = await AuthedJson(HttpMethod.Put, Route, new { mission = "Snooze Length", why = "" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var doc = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("cleared").GetBoolean());

        Assert.Null(await GetWhy("Snooze Length"));
    }

    // ---- 2b. DURABLE + SHARED -----------------------------------------------------------------

    [Fact]
    public async Task A_why_is_read_by_a_fresh_client_and_survives_a_restart()
    {
        using (var set = await AuthedJson(HttpMethod.Put, Route, new { mission = "Mission Screen", why = "keep the owner oriented across the fleet" }))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // A FRESH client (a different browser) against the SAME running Gateway sees the same WHY - it is
        // shared, not per-browser.
        using (var fresh = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") })
        {
            fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            Assert.Equal("keep the owner oriented across the fleet", await GetWhy("Mission Screen", fresh));
        }

        // A Gateway restart: a second host over the SAME store file re-serves the WHY - it is durable.
        await _gateway.StopAsync();
        var restarted = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: InstancesDir,
            workListsPath: Path.Combine(_dir, "worklists.json"),
            missionNotesPath: MissionNotesPath);
        await restarted.StartAsync();
        try
        {
            using var afterRestart = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{restarted.Port}/") };
            afterRestart.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            Assert.Equal("keep the owner oriented across the fleet", await GetWhy("Mission Screen", afterRestart));
        }
        finally
        {
            await restarted.StopAsync();
            // Re-arm the field host so DisposeAsync's StopAsync is a no-op-safe double stop on a stopped host.
            _gateway = restarted;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private Task<HttpResponseMessage> Authed(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> AuthedJson(HttpMethod method, string path, object body)
    {
        var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return _http.SendAsync(req);
    }

    // The WHY currently stored for a mission (via the authenticated GET-all), or null when none is set.
    private async Task<string?> GetWhy(string mission, HttpClient? client = null)
    {
        var http = client ?? _http;
        using var req = new HttpRequestMessage(HttpMethod.Get, Route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var key = mission.Trim().ToLowerInvariant();
        foreach (var n in doc.RootElement.GetProperty("notes").EnumerateArray())
        {
            if (n.GetProperty("key").GetString() == key)
                return n.GetProperty("why").GetString();
        }
        return null;
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
