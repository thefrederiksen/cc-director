using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP tests for the mission WHY now that it lives ON the Mission record, over a real Gateway with auth
/// ON. This file REPLACES MissionNotesEndpointTests, which covered <c>/gateway/missions/notes</c> - a
/// surface that keyed the WHY by the mission's LOWER-CASED NAME and did no tenant resolution of its own.
///
/// Four things are proven here rather than assumed:
///  1. AUTH: an unauthenticated PATCH is refused by the host-wide device-key middleware, and refused
///     WITHOUT applying the write. A wrong assumption here would leave every mission's WHY world-writable.
///  2. THE OLD SURFACE IS GONE. The retired routes are asserted to 404, so "we removed it" is a fact about
///     the running Gateway rather than a claim about the source. A retirement nobody checks is how two
///     ways to write the same field survive side by side.
///  3. ROUND TRIP: a WHY set through one authenticated client reads back on GET /missions, and survives a
///     Gateway restart over the same store file - the mission's promise that a WHY is durable and shared,
///     not per-browser.
///  4. IT IS KEYED BY ID. The WHY is written against the mission id and read back off that mission, which
///     is the whole point of the move: a name-keyed WHY was orphaned by any rename.
/// </summary>
public sealed class MissionWhyEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-mission-why";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-mission-why-" + Guid.NewGuid().ToString("N"));
    private string InstancesDir => Path.Combine(_dir, "instances");
    private string MissionsPath => Path.Combine(_dir, "missions.json");

    public async Task InitializeAsync()
    {
        _gateway = await StartHost();
        _http = NewClient(_gateway);
    }

    private async Task<GatewayHost> StartHost()
    {
        var host = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: InstancesDir,
            workListsPath: Path.Combine(_dir, "worklists.json"),
            missionNotesPath: Path.Combine(_dir, "mission-notes.json"),
            missionsPath: MissionsPath);
        await host.StartAsync();
        return host;
    }

    private static HttpClient NewClient(GatewayHost host) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/"),
        };

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- 1. AUTH -------------------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_PATCH_is_refused_and_changes_nothing()
    {
        var mission = await CreateMission("Release 2.0.1");

        using var res = await _http.PatchAsJsonAsync($"/missions/{mission}", new { why = "sneaky" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        // Refused, not silently applied - the distinction the test exists for.
        Assert.Equal("", await GetWhy(mission));
    }

    // ---- 2. THE RETIRED SURFACE IS ACTUALLY GONE -----------------------------------------------------

    [Fact]
    public async Task The_old_name_keyed_notes_routes_no_longer_exist()
    {
        // Asserted against the RUNNING Gateway. A second way to write the WHY - under a weaker boundary and
        // a key that a rename orphans - is exactly the kind of leftover this whole area needed untangling
        // for, so its absence is checked rather than assumed.
        using var get = await Authed(HttpMethod.Get, "/gateway/missions/notes");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Put, "/gateway/missions/notes")
        {
            Content = JsonContent.Create(new { mission = "Release 2.0.1", why = "via the old route" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var put = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    // ---- 3 + 4. ROUND TRIP, KEYED BY ID, DURABLE -----------------------------------------------------

    [Fact]
    public async Task Why_set_by_id_reads_back_and_survives_a_restart()
    {
        var mission = await CreateMission("Release 2.0.1");

        var patched = await PatchWhy(mission, "So we can get the Video Competition started");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        Assert.Equal("So we can get the Video Competition started", await GetWhy(mission));

        // A second host over the same missions.json models a Gateway restart: the WHY is durable, not
        // per-process and not per-browser.
        await _gateway.StopAsync();
        _http.Dispose();
        _gateway = await StartHost();
        _http = NewClient(_gateway);

        Assert.Equal("So we can get the Video Competition started", await GetWhy(mission));
    }

    [Fact]
    public async Task Blank_why_clears_it()
    {
        var mission = await CreateMission("Release 2.0.1");
        await PatchWhy(mission, "a reason");
        Assert.Equal("a reason", await GetWhy(mission));

        var cleared = await PatchWhy(mission, "   ");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        // Empty means UNSET, so the card returns to its loud flag rather than showing a blank.
        Assert.Equal("", await GetWhy(mission));
    }

    [Fact]
    public async Task Unknown_mission_is_404()
    {
        var res = await PatchWhy(Guid.NewGuid().ToString(), "why");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_body_with_no_why_is_rejected_rather_than_silently_doing_nothing()
    {
        var mission = await CreateMission("Release 2.0.1");

        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/missions/{mission}")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Authed(HttpMethod method, string route)
    {
        using var req = new HttpRequestMessage(method, route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req);
    }

    private async Task<HttpResponseMessage> PatchWhy(string missionId, string why)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/missions/{missionId}")
        {
            Content = JsonContent.Create(new { why }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req);
    }

    private async Task<string> CreateMission(string name)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/missions")
        {
            Content = JsonContent.Create(new { missionName = name }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("missionId").GetString()!;
    }

    /// <summary>The mission's WHY as GET /missions reports it - "" when unset.</summary>
    private async Task<string> GetWhy(string missionId)
    {
        using var res = await Authed(HttpMethod.Get, $"/missions/{missionId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("why", out var why) ? why.GetString() ?? "" : "";
    }
}
