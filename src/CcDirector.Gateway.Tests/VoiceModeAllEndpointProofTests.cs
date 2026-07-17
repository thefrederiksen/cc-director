using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1765 - the fleet-wide voice switch. POST /sessions/voice-mode/all walks the whole roster and, for
/// each session, sends the owning Director the same "voice-mode" verb the per-session path sends, then marks
/// (or unmarks) the session on the Gateway. This boots a REAL streamMode <see cref="GatewayHost"/>, dials the
/// REAL DirectorHub with a REAL MessagePack client, PUSHES the sessions so their owner resolves with zero
/// remote reach, and drives the batch endpoint.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered UNREACHABLE and the sessions arrive ONLY via
/// PushSnapshot, so a session that reports "on" can only have ridden the tunnel - an HTTP dial to the
/// unreachable control endpoint would have failed and the owner would never have resolved. The two tests
/// prove the happy path (every session switched, one voice-mode command per session carrying the enabled
/// flag) and the skip path (a session whose Director rejects the write is skipped and reported, never
/// failing the batch).
/// </summary>
[Collection("DirectorRoot")]
public sealed class VoiceModeAllEndpointProofTests : IAsyncLifetime
{
    private const string Token = "test-token-voice-mode-all-proof";
    private const string DirectorId = "dir-voice-mode-all";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-vmall-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    // Every command the fake Director received, captured concurrently (the fan-out sends them in parallel).
    private readonly ConcurrentBag<DirectorCommand> _commands = new();
    // Session ids the fake Director should REJECT the voice-mode write for (to prove the skip path).
    private readonly HashSet<string> _rejectSids = new(StringComparer.Ordinal);

    public VoiceModeAllEndpointProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-vmall-" + Guid.NewGuid().ToString("N"));
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

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59923/", // nothing listens here
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
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

    // The fake Director accepts the voice-mode write (Success) for every session except those in _rejectSids,
    // which it fails - proving a rejected session is reported skipped without failing the whole batch.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _commands.Add(cmd);
        if (cmd.Verb != "voice-mode")
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        if (_rejectSids.Contains(cmd.SessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");
        return DirectorCommandResult.Success("{}");
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    private static SessionDto Session(string sid, string name) => new()
    {
        SessionId = sid,
        Name = name,
        Status = "WaitingForInput",
        ActivityState = "WaitingForInput",
        RepoPath = @"D:\repo",
    };

    [Fact]
    public async Task VoiceModeAll_enable_switchesEverySession_overTheTunnel()
    {
        var sid1 = Guid.NewGuid().ToString();
        var sid2 = Guid.NewGuid().ToString();
        await PushAsync(1L, Session(sid1, "one"), Session(sid2, "two"));

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["enabled"]?.GetValue<bool>());
        Assert.Equal(2, node?["total"]?.GetValue<int>());
        Assert.Equal(2, node?["changed"]?.GetValue<int>());
        Assert.Equal(0, node?["skipped"]?.GetValue<int>());

        // Exactly one voice-mode write per session, each carrying enabled=true, and they rode the tunnel
        // (an HTTP dial to the unreachable Director could never have resolved the owner).
        var voiceModeCmds = _commands.Where(c => c.Verb == "voice-mode").ToList();
        Assert.Equal(2, voiceModeCmds.Count);
        Assert.Contains(voiceModeCmds, c => c.SessionId == sid1);
        Assert.Contains(voiceModeCmds, c => c.SessionId == sid2);
        foreach (var c in voiceModeCmds)
            Assert.True(JsonNode.Parse(c.PayloadJson)?["enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task VoiceModeAll_skipsASessionWhoseDirectorRejects_withoutFailingTheBatch()
    {
        var ok = Guid.NewGuid().ToString();
        var rejected = Guid.NewGuid().ToString();
        _rejectSids.Add(rejected);
        await PushAsync(1L, Session(ok, "keeps"), Session(rejected, "rejects"));

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(2, node?["total"]?.GetValue<int>());
        Assert.Equal(1, node?["changed"]?.GetValue<int>());
        Assert.Equal(1, node?["skipped"]?.GetValue<int>());

        // The per-session report names the rejected one as not-ok with a reason, and the other as ok.
        var sessions = node?["sessions"]?.AsArray() ?? new JsonArray();
        var okRow = sessions.FirstOrDefault(s => s?["sessionId"]?.GetValue<string>() == ok);
        var rejectedRow = sessions.FirstOrDefault(s => s?["sessionId"]?.GetValue<string>() == rejected);
        Assert.True(okRow?["ok"]?.GetValue<bool>());
        Assert.False(rejectedRow?["ok"]?.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(rejectedRow?["reason"]?.GetValue<string>()));
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
