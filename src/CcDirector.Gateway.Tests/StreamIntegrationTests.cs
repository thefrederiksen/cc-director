using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end integration harness for the Phase 1a push stream (issue #1176). Boots a real GatewayHost
/// with stream mode ON, dials the DirectorHub with a real SignalR client, pushes state, and asserts the
/// aggregated <c>GET /sessions</c> serves that state from the pushed cache. The Director is registered
/// with a DELIBERATELY UNREACHABLE control endpoint, so any session that appears in <c>/sessions</c> with
/// no machine error can only have come from the cache - a pull would have failed. This proves the whole
/// Gateway side (hub auth, store, aggregation dual-mode) works over the wire.
/// </summary>
[Collection("DirectorRoot")]
public sealed class StreamIntegrationTests : IAsyncLifetime
{
    private const string Token = "test-token-stream-1176";
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-stream-int-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;   // set in InitializeAsync
    private HttpClient _http = null!;       // set in InitializeAsync

    public StreamIntegrationTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-stream-int-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task StreamPush_IsServedFromCache_WithoutPullingTheDirector()
    {
        RegisterDirector("dir-A", "http://127.0.0.1:59991/"); // unreachable on purpose
        await using var conn = await ConnectDirectorAsync(Token);
        await conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });
        await conn.InvokeAsync("PushSnapshot", 0L, new[] { Session("s1"), Session("s2") });

        var (sessionIds, errorDirectors) = await GetSessionsAsync();

        Assert.Contains("s1", sessionIds);
        Assert.Contains("s2", sessionIds);
        Assert.DoesNotContain("dir-A", errorDirectors); // never pulled => no unreachable error
    }

    [Fact]
    public async Task PushDelta_IsReflectedInSessions()
    {
        RegisterDirector("dir-A", "http://127.0.0.1:59991/");
        await using var conn = await ConnectDirectorAsync(Token);
        await conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });
        await conn.InvokeAsync("PushSnapshot", 1L, new[] { Session("s1", "Working") });

        await conn.InvokeAsync("PushDelta", 2L, Session("s1", "WaitingForInput"));

        var state = await GetSessionStateAsync("s1");
        Assert.Equal("WaitingForInput", state);
    }

    [Fact]
    public async Task RemoveSession_IsReflectedInSessions()
    {
        RegisterDirector("dir-A", "http://127.0.0.1:59991/");
        await using var conn = await ConnectDirectorAsync(Token);
        await conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });
        await conn.InvokeAsync("PushSnapshot", 1L, new[] { Session("s1"), Session("s2") });

        await conn.InvokeAsync("RemoveSession", 2L, "s1");

        var (sessionIds, _) = await GetSessionsAsync();
        Assert.DoesNotContain("s1", sessionIds);
        Assert.Contains("s2", sessionIds);
    }

    [Fact]
    public async Task UnauthenticatedConnect_IsRejected()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream") // no token
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => conn.StartAsync());
        await conn.DisposeAsync();
    }

    // Gateway Cleanup mission (the cut): the streamMode-OFF negative test was removed. The tunnel is now
    // MANDATORY - the DirectorHub is always mapped, there is no HTTP-fallback mode to keep it unmapped for.
    // The streamMode ctor parameter is retained (ignored) only until the remaining test call sites drop it.

    private void RegisterDirector(string directorId, string endpoint) =>
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = directorId,
            TailnetEndpoint = endpoint,
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

    private async Task<HubConnection> ConnectDirectorAsync(string token)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await conn.StartAsync();
        return conn;
    }

    private static SessionDto Session(string id, string state = "Working") =>
        new() { SessionId = id, ActivityState = state };

    private async Task<(List<string> sessionIds, List<string> errorDirectors)> GetSessionsAsync()
    {
        var resp = await _http.GetAsync("sessions?envelope=true");
        resp.EnsureSuccessStatusCode();
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();

        var sessionsArray = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        var sessionIds = new List<string>();
        foreach (var s in sessionsArray)
        {
            var id = s?["sessionId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) sessionIds.Add(id);
        }

        var errors = new List<string>();
        var errorArray = node?["machineErrors"]?.AsArray();
        if (errorArray is not null)
        {
            foreach (var e in errorArray)
            {
                var id = e?["directorId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) errors.Add(id);
            }
        }
        return (sessionIds, errors);
    }

    private async Task<string?> GetSessionStateAsync(string sessionId)
    {
        var resp = await _http.GetAsync("sessions?envelope=true");
        resp.EnsureSuccessStatusCode();
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        var sessionsArray = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        foreach (var s in sessionsArray)
        {
            if (s?["sessionId"]?.GetValue<string>() == sessionId)
                return s?["activityState"]?.GetValue<string>();
        }
        return null;
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
