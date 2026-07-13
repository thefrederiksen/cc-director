using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E, Group C): the Gateway roster PULLS that used to call
/// <c>DirectorEndpointClient.ListSessions*</c> now read the PUSH store (<c>PushedSessions</c>) under stream mode,
/// per the Architect ruling (the roster is authoritative in the push store; no pull verb). This covers the three
/// pure roster reads: <c>/healthz</c> session count, the <c>/exes/list</c> per-Director session list, and the
/// <c>DELETE /directors/{id}</c> live-session safety gate.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered UNREACHABLE and its sessions are delivered ONLY via a
/// stream PushSnapshot. So a result that reflects those sessions can ONLY have come from the push store - an
/// HTTP pull to the unreachable Director would have returned nothing.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelRosterPushReadProofTests : IAsyncLifetime
{
    private const string Token = "test-token-roster-push-read";
    private const string DirectorId = "dir-roster-push";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-rosterpush-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    public TunnelRosterPushReadProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-rosterpush-" + Guid.NewGuid().ToString("N"));
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

        // Registered UNREACHABLE, but on THIS machine so /exes/list (local-machine only) surfaces it.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59920/", // nothing listens here
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    [Fact]
    public async Task Healthz_countsSessionsFromThePushStore()
    {
        await PushAsync(1L,
            new SessionDto { SessionId = Guid.NewGuid().ToString(), Status = "WaitingForInput", ActivityState = "WaitingForInput" },
            new SessionDto { SessionId = Guid.NewGuid().ToString(), Status = "Working", ActivityState = "Working" });

        var node = await _http.GetFromJsonAsync<JsonNode>("healthz");
        // An HTTP pull to the unreachable Director would count 0; the push store carries 2.
        Assert.Equal(2, node?["sessions"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExesList_readsTheDirectorSessionsFromThePushStore()
    {
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid,
            Name = "a pushed session",
            Agent = "ClaudeCode",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });

        var node = await _http.GetFromJsonAsync<JsonNode>("exes/list");
        var directors = node?["directors"]?.AsArray();
        var mine = directors?.FirstOrDefault(d => d?["directorId"]?.GetValue<string>() == DirectorId);
        Assert.NotNull(mine);
        var sessions = mine!["sessions"]?.AsArray();
        Assert.Equal(1, sessions?.Count);
        Assert.Equal(sid, sessions?[0]?["sessionId"]?.GetValue<string>());
        Assert.Null(mine["sessionError"]?.GetValue<string?>()); // no pull error - it never pulled
    }

    [Fact]
    public async Task DeleteDirectorGate_readsTheLiveSessionCountFromThePushStore()
    {
        // One live session in the push store. An HTTP pull to the unreachable Director would return null and the
        // gate would be SKIPPED (deletion proceeds); a 409 citing the live count proves the push-store read.
        await PushAsync(1L, new SessionDto
        {
            SessionId = Guid.NewGuid().ToString(),
            Name = "a live session",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });

        var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{DirectorId}")
        {
            Content = JsonContent.Create(new { reason = "cleanup test", force = false }),
        };
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(1, node?["liveSessionCount"]?.GetValue<int>());
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
