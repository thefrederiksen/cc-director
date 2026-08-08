using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Routing;
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
        //
        // NOT asserted by status code (issue #2516). This test used to expect 404 on both verbs, and that
        // expectation was really a statement about something else entirely: the Cockpit single-page app is
        // registered as MapFallback("{*path}"), and a fallback answers any path no other endpoint matched -
        // the shell with 200 when the Cockpit is built into the host, 404 only when it is not. So a 404 here
        // meant "this build has no Cockpit", and the moment a Release build staged one the test flipped to
        // failing while nothing about the retirement had changed. It then failed on main for a day, in the
        // release gate and on continuous integration, saying a retired route was alive when it was not.
        //
        // Three checks replace it, each proving a different thing, and each is narrower than "nothing can
        // ever serve this WHY again" - state what they do prove:
        //
        //  1. NO ROUTE IS REGISTERED AT THAT EXACT PATTERN, read off the running Gateway's finalised route
        //     table. This is the one that catches the retirement being undone by re-registering the endpoint;
        //     it does NOT prove nothing MATCHES the path - the fallback matches it by design, and so would a
        //     parameterised route such as /gateway/missions/{x}.
        //  2. NEITHER VERB RETURNS THE RETIRED PAYLOAD over real HTTP. This fingerprints the shape the old
        //     endpoint served; a re-implementation answering some other shape would pass it.
        //  3. THE WRITE DOES NOT LAND on a mission that already exists, which is the consequence that would
        //     actually hurt: the retired writer keyed by the mission's lower-cased NAME, so the mission is
        //     created FIRST, with a WHY set, and the retired PUT is aimed at that name.
        const string RetiredPath = "/gateway/missions/notes";

        var registered = _gateway.MappedEndpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'))
            .Where(p => p.Equals(RetiredPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(registered);

        using var get = await Authed(HttpMethod.Get, RetiredPath);
        Assert.False(IsNotesPayload(await get.Content.ReadAsStringAsync()),
            "GET " + RetiredPath + " served the retired notes payload");

        // The mission exists BEFORE the retired write is attempted, and carries a WHY of its own. Attempting
        // the write first would let an implementation that resolves an existing mission by name pass simply
        // because there was nothing to resolve.
        var mission = await CreateMission("Release 2.0.1");
        await PatchWhy(mission, "set through the route that is not retired");
        Assert.Equal("set through the route that is not retired", await GetWhy(mission));

        using var req = new HttpRequestMessage(HttpMethod.Put, RetiredPath)
        {
            Content = JsonContent.Create(new { mission = "Release 2.0.1", why = "via the old route" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var put = await _http.SendAsync(req);
        Assert.False(IsNotesPayload(await put.Content.ReadAsStringAsync()),
            "PUT " + RetiredPath + " served the retired note payload");

        // Unchanged - asserted against the value that was deliberately put there, so this cannot pass by
        // reading an absent field as an empty string.
        Assert.Equal("set through the route that is not retired", await GetWhy(mission));
    }

    /// <summary>
    /// Whether a response body is the retired notes surface answering, rather than the Cockpit shell or a
    /// refusal. The retired reader served <c>{"notes":[...]}</c> and the writer served <c>{"note":...}</c>,
    /// so the JSON shape is what identifies it - a body that is not JSON at all cannot be it.
    ///
    /// This fingerprints THAT implementation, not the capability: a different surface answering some other
    /// shape would not be recognised here. The registration check above and the unchanged-WHY check below
    /// are what cover the ways this one cannot.
    /// </summary>
    private static bool IsNotesPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && (doc.RootElement.TryGetProperty("notes", out _)
                       || doc.RootElement.TryGetProperty("note", out _));
        }
        catch (JsonException)
        {
            return false;
        }
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

    // ---- rename and ending (Phase 2) ------------------------------------------------------------------

    [Fact]
    public async Task Rename_changes_the_name_and_keeps_the_why()
    {
        var mission = await CreateMission("Release 2.0.0");
        await PatchWhy(mission, "the reason");

        using var res = await Patch(mission, new { missionName = "  Release 2.0.1  " });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var listed = await GetMission(mission);
        Assert.Equal("Release 2.0.1", listed.GetProperty("missionName").GetString());
        // The WHY survives the rename. Under the old name-keyed store it would simply have vanished.
        Assert.Equal("the reason", listed.GetProperty("why").GetString());
    }

    [Fact]
    public async Task A_blank_name_is_rejected_rather_than_stored()
    {
        var mission = await CreateMission("Release 2.0.1");

        using var res = await Patch(mission, new { missionName = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var listed = await GetMission(mission);
        Assert.Equal("Release 2.0.1", listed.GetProperty("missionName").GetString());
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("removed")]
    public async Task Ending_a_mission_takes_it_out_of_the_default_list_but_keeps_the_record(string ending)
    {
        var mission = await CreateMission("Remove the network port");

        using var res = await Patch(mission, new { state = ending });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.DoesNotContain(mission, await ListMissionIds(null));
        Assert.Contains(mission, await ListMissionIds("all"));
        Assert.Contains(mission, await ListMissionIds(ending));

        // Soft: the record is still addressable by id.
        var listed = await GetMission(mission);
        Assert.Equal(ending, listed.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_ended_mission_can_be_reopened()
    {
        var mission = await CreateMission("Ended by mistake");
        await Patch(mission, new { state = "complete" });
        Assert.DoesNotContain(mission, await ListMissionIds(null));

        using var res = await Patch(mission, new { state = "active" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(mission, await ListMissionIds(null));
    }

    // The run store refuses created -> succeeded, and EVERY mission run on this fleet is still at created
    // because nothing has ever advanced one. So the ordinary case is the one needing two steps, and this is
    // the test that would catch it silently not happening.
    [Fact]
    public async Task Completing_a_mission_drives_its_workflow_run_to_a_terminal_state()
    {
        var mission = await CreateMission("Release 2.0.1");
        Assert.Equal("created", await RunStatusFor(mission));

        using var res = await Patch(mission, new { state = "complete" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("succeeded", await RunStatusFor(mission));
    }

    [Fact]
    public async Task Removing_a_mission_abandons_its_workflow_run()
    {
        var mission = await CreateMission("A duplicate");

        using var res = await Patch(mission, new { state = "removed" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("abandoned", await RunStatusFor(mission));
    }

    [Fact]
    public async Task An_unrecognised_state_is_rejected()
    {
        var mission = await CreateMission("Release 2.0.1");
        using var res = await Patch(mission, new { state = "archived" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_state_filter_the_route_does_not_know_is_rejected()
    {
        using var res = await Authed(HttpMethod.Get, "/missions?state=parked");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Phase 2 helpers -----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Patch(string missionId, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/missions/{missionId}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req);
    }

    private async Task<JsonElement> GetMission(string missionId)
    {
        using var res = await Authed(HttpMethod.Get, $"/missions/{missionId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<List<string>> ListMissionIds(string? state)
    {
        var route = state is null ? "/missions" : $"/missions?state={state}";
        using var res = await Authed(HttpMethod.Get, route);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("missionId").GetString()!)
            .ToList();
    }

    private async Task<string> RunStatusFor(string missionId)
    {
        using var res = await Authed(HttpMethod.Get, $"/gateway/workflow-runs?missionId={missionId}&limit=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var runs = doc.RootElement.TryGetProperty("runs", out var r) ? r : doc.RootElement;
        var first = runs.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Undefined ? "(no run)" : first.GetProperty("status").GetString()!;
    }
}
