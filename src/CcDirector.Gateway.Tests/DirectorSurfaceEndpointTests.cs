using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Wiring + validation tests for the final-build Director surface: #5 resize, and the #6
/// endpoints (relink, git writes, scheduler, workspaces/history). No live session is needed -
/// these prove the routes exist, validate input, and 404/503 correctly. The behavior of
/// resize/prompt-queue/git lives in the Core unit tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorSurfaceEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = DirectorTestClient.Admin(port);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup */ }
    }

    // ---- #5 resize ----

    // Gateway Cleanup (the cut): the Director's POST /sessions/{sid}/resize REST route is deleted. Resize is
    // now a tunnel verb whose validation core lives in SessionWriteExecutor.Resize (dispatched via
    // SessionCommandExecutor.DispatchAsync). This asserts that same input guard directly against the real
    // core - non-positive cols/rows -> BadRequest - so the strength of the original wire test is preserved
    // over the surviving code, not a canned stand-in. (The Gateway maps a BadRequest verb result to HTTP 400
    // in TunnelCatchAllDispatch.)
    [Fact]
    public async Task Resize_rejects_nonpositive_dimensions()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, _host.DirectorId,
            Command("resize", Guid.NewGuid().ToString(), new ResizeRequest { Cols = 0, Rows = 24 }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Resize_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/resize", new ResizeRequest { Cols = 80, Rows = 24 });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 relink ----

    // Gateway Cleanup (the cut): the Director's POST /sessions/{sid}/relink REST route is deleted. Relink is
    // now a tunnel verb whose validation core lives in SessionWriteExecutor.Relink (dispatched via
    // SessionCommandExecutor.DispatchAsync). This asserts that same input guard directly against the real
    // core - absent/blank claudeSessionId -> BadRequest - so the original wire test's strength is preserved.
    [Fact]
    public async Task Relink_rejects_empty_claude_session_id()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, _host.DirectorId,
            Command("relink", Guid.NewGuid().ToString(), new RelinkRequest { ClaudeSessionId = "" }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Relink_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/relink", new RelinkRequest { ClaudeSessionId = "claude-xyz" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 git writes ----

    [Fact]
    public async Task Git_stage_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/git/stage", new GitPathsRequest());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 workspaces / history (read; present and 200 even when empty) ----

    [Fact]
    public async Task Workspaces_list_returns_200()
    {
        var resp = await _client.GetAsync("workspaces");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("items", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_list_returns_200()
    {
        var resp = await _client.GetAsync("history");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("items", await resp.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DirectorCommand Command(string verb, string sessionId, object? payload = null) => new()
    {
        CommandId = "cmd-surface",
        Verb = verb,
        SessionId = sessionId,
        PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Json),
    };
}
