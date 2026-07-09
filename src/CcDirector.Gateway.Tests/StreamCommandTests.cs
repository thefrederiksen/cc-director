using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1177 (Phase 1, increment 1): end-to-end proof that a command flows DOWN the Director stream and
/// takes effect. A real Gateway (stream mode ON) drives a real Director-side <see cref="GatewayStreamClient"/>
/// whose down-channel dispatcher is the shared <see cref="SessionCommandExecutor"/> over a real
/// <see cref="SessionManager"/>. Covers: the direct down-channel (<c>SendCommandAsync</c>), the Gateway
/// prompt endpoint routing DOWN the stream (proven by a dispatcher spy the HTTP fallback never touches),
/// and the flag-off regression (stream mode off => the endpoint stays on HTTP).
/// </summary>
[Collection("DirectorRoot")]
public sealed class StreamCommandTests : IAsyncLifetime
{
    private const string Token = "test-token-streamcmd-1177";
    private const string DirectorId = "dir-A";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _gatewayInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-dir-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;      // set in InitializeAsync
    private HttpClient _http = null!;          // set in InitializeAsync
    private SessionManager _directorSessions = null!;
    private ControlApiHost _directorHost = null!;
    private int _directorPort;

    public StreamCommandTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-streamcmd-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _gatewayInstances,
            workListsPath: Path.Combine(_gatewayInstances, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A real Director-side Control API so the Gateway can LOCATE the session over HTTP (Phase 1 keeps
        // session location on HTTP; only command delivery moves to the stream). Auth off so the Gateway's
        // endpoint client can read GET /sessions/{sid} without a token in this harness.
        _directorSessions = new SessionManager(new AgentOptions());
        _directorHost = new ControlApiHost(_directorSessions, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: false, directorId: DirectorId, instancesDirectory: _directorInstances);
        _directorPort = await _directorHost.StartAsync();

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = $"http://127.0.0.1:{_directorPort}/",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _directorHost.StopAsync();
        _directorSessions.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        TryDelete(_gatewayInstances);
        TryDelete(_directorInstances);
        TryDelete(_root);
    }

    [Fact]
    public async Task SendCommandAsync_Prompt_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "over-the-stream", AppendEnter = false });
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Contains("over-the-stream", BufferText(session));
    }

    [Fact]
    public async Task SendCommandAsync_ReturnsNull_WhenDirectorHasNoStream()
    {
        // No GatewayStreamClient started for this id => no active connection => null (HTTP fallback signal).
        var result = await _gateway.SendCommandAsync("dir-nobody",
            PromptCommand(Guid.NewGuid().ToString(), new PromptRequest { Text = "x" }));
        Assert.Null(result);
    }

    [Fact]
    public async Task GatewayPromptEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/prompt", new PromptRequest { Text = "endpoint-stream", AppendEnter = false });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.NotNull(body);
        Assert.True(body.Accepted);
        Assert.Contains("endpoint-stream", BufferText(session));
        // The spy sits ONLY on the stream down-channel. A count of 1 proves the endpoint delivered the
        // prompt over the stream; an HTTP fallback would have gone straight to the Director's own endpoint
        // and never touched this dispatcher.
        Assert.Equal(1, spy.Count);
    }

    [Fact]
    public async Task SendCommandAsync_Interrupt_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "interrupt", SessionId = session.Id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        // Claude's driver writes Ctrl+C (0x03) to the backend: proof the interrupt executed on the Director.
        Assert.Contains((byte)0x03, BufferRaw(session));
    }

    [Fact]
    public async Task SendCommandAsync_Escape_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "escape", SessionId = session.Id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        // Claude's driver writes Esc (0x1B) to the backend: proof the escape executed on the Director.
        Assert.Contains((byte)0x1B, BufferRaw(session));
    }

    [Fact]
    public async Task GatewayInterruptEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsync($"sessions/{session.Id}/interrupt", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains((byte)0x03, BufferRaw(session));
        Assert.Equal(1, spy.Count); // delivered over the stream, not the Director's HTTP endpoint
    }

    [Fact]
    public async Task GatewayEscapeEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsync($"sessions/{session.Id}/escape", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains((byte)0x1B, BufferRaw(session));
        Assert.Equal(1, spy.Count);
    }

    [Fact]
    public async Task SendCommandAsync_Hold_RoundTripsAndSetsOnHold()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "hold",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new HoldRequest { OnHold = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.True(session.OnHold);
        var body = JsonSerializer.Deserialize<HoldResponse>(result.BodyJson ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.True(body.OnHold);
    }

    [Fact]
    public async Task SendCommandAsync_Kill_RoundTripsAndRemovesTheSession()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var id = session.Id;
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "kill", SessionId = id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Null(_directorSessions.GetSession(id)); // killed + removed on the Director
    }

    [Fact]
    public async Task GatewayHoldEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/hold", new HoldRequest { OnHold = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(body);
        Assert.True(body.OnHold);
        Assert.True(session.OnHold);
        Assert.Equal(1, spy.Count);
    }

    [Fact]
    public async Task GatewayKillEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var id = session.Id;
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.DeleteAsync($"sessions/{id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(_directorSessions.GetSession(id)); // removed over the stream
        Assert.Equal(1, spy.Count);
    }

    [Fact]
    public async Task SendCommandAsync_Patch_RoundTripsAndRenamesTheSession()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "patch",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "Stream-Rename" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal("Stream-Rename", session.CustomName);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dto);
        Assert.Equal("Stream-Rename", dto.Name);
    }

    [Fact]
    public async Task GatewayPatchEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PatchAsJsonAsync($"sessions/{session.Id}", new SessionUpdateRequest { Name = "Endpoint-Rename" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(body);
        Assert.Equal("Endpoint-Rename", body.Name);       // the returned DTO carries the new name
        Assert.Equal(DirectorId, body.DirectorId);         // the Gateway stamped the DirectorId
        Assert.Equal("Endpoint-Rename", session.CustomName); // and it actually took effect on the Director
        Assert.Equal(1, spy.Count);                        // delivered over the stream, not HTTP
    }

    [Fact]
    public async Task StreamModeOff_PatchEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offp-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offp", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offp");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offp",
                TailnetEndpoint = "http://127.0.0.1:59997/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PatchAsJsonAsync($"sessions/{Guid.NewGuid()}", new SessionUpdateRequest { Name = "x" });

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task SendCommandAsync_WingmanGoal_RoundTripsAndSetsGoal()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "wingman-goal",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "stream-goal" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal("stream-goal", session.WingmanGoal); // set on the Director over the stream
        Assert.Contains("stream-goal", result.BodyJson ?? "");
    }

    [Fact]
    public async Task GatewayWingmanGoalEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/wingman/goal", new WingmanGoalRequest { Goal = "endpoint-goal" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("endpoint-goal", text);
        Assert.Equal("endpoint-goal", session.WingmanGoal); // took effect on the Director
        Assert.Equal(1, spy.Count);                          // delivered over the stream, not HTTP
    }

    [Fact]
    public async Task StreamModeOff_WingmanGoalEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offg-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offg", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offg");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offg",
                TailnetEndpoint = "http://127.0.0.1:59999/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/wingman/goal", new WingmanGoalRequest { Goal = "x" });

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task PortlessDirector_PerSessionPrompt_LocatesViaPushedCache_AndRoutesDownTheStream()
    {
        // Phase 4a: a remotely-unreachable (portless) Director. Its control endpoint is empty, so the
        // HTTP-pull location path CANNOT find its sessions (mirrors tailscale-off, controlEndpoint=""). The
        // session is created BEFORE the stream connects so the initial snapshot carries it into the Gateway's
        // pushed cache - the ONLY way to locate it. The per-session prompt must resolve the owner from that
        // pushed cache and route DOWN the stream, with zero HTTP reach into the Director.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());

        // Re-register dir-A with NO reachable endpoint (portless: nothing to pull).
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        Assert.Equal("", _gateway.Registry.Get(DirectorId)?.ControlEndpoint);

        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/prompt",
            new PromptRequest { Text = "portless-stream", AppendEnter = false });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.NotNull(body);
        Assert.True(body.Accepted);
        Assert.Contains("portless-stream", BufferText(session)); // took effect on the Director
        Assert.Equal(1, spy.Count); // located from the pushed cache + delivered over the stream, zero HTTP
    }

    [Fact]
    public async Task PortlessDirector_PerSessionHold_LocatesViaPushedCache_AndRoutesDownTheStream()
    {
        // A second per-session verb over the portless path, proving location (not just prompt) is fixed.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/hold", new HoldRequest { OnHold = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(body);
        Assert.True(body.OnHold);
        Assert.True(session.OnHold);
        Assert.Equal(1, spy.Count);
    }

    [Fact]
    public async Task StreamModeOff_PortlessDirector_PerSessionPrompt_Returns404_LocationUnchanged()
    {
        // Flag-off regression: with stream mode OFF, pushedSessions is null, so LocateSessionAsync uses ONLY
        // the HTTP pull - which against an empty control endpoint finds nothing => 404, exactly as today. This
        // pins that the pushed-cache location is INERT when the flag is off (byte-identical behaviour).
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offloc-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offloc", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offloc");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offloc",
                TailnetEndpoint = "", // portless: nothing to pull
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task PeriodicRePush_KeepsPushedCacheFresh_ForAQuietSession()
    {
        // Phase 4a Fix 2: a quiet session (no deltas after the initial snapshot). With a SHORT re-push
        // interval, the Director keeps re-sending its full snapshot, so the Gateway's pushed cache stays
        // fresh against a stale window SHORTER than the total elapsed time - which only the periodic re-push
        // can achieve (portless has no HTTP pull floor).
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions), rePushInterval: TimeSpan.FromMilliseconds(100));
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        // Elapsed ~1.2s at a 500ms stale window: without re-push the cache would go stale after ~500ms and
        // this loop would fail; the 100ms re-push keeps ReceivedAtUtc well inside the window throughout.
        var stale = TimeSpan.FromMilliseconds(500);
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100);
            Assert.NotNull(_gateway.PushedSessions.TryGetFresh(DirectorId, stale));
        }
    }

    [Fact]
    public async Task StreamModeOff_NoPeriodicRePush_PushedCacheStaysEmpty()
    {
        // Flag-off regression: a stream-mode-OFF client's Start() is a no-op (IsEnabled false), so no timer is
        // armed and nothing is ever pushed - byte-identical to a Gateway with no stream at all.
        var config = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token, StreamMode = false };
        await using var client = new GatewayStreamClient(config, "dir-off-repush", "test", SnapshotDirectorSessions,
            rePushInterval: TimeSpan.FromMilliseconds(100));
        client.Start();

        // Even after several would-be re-push intervals, this Director never connected and never pushed.
        await Task.Delay(500);
        Assert.False(_gateway.PushedSessions.IsStreamConnected("dir-off-repush"));
        Assert.Null(_gateway.PushedSessions.TryGetFresh("dir-off-repush", TimeSpan.FromSeconds(30)));
    }

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path (ConPty
    // spawn) without depending on an installed coding-agent CLI.
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    [Fact]
    public async Task GatewayCreateEndpoint_RoutesDownTheStream_CreatesRealSessionOnTheDirector()
    {
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var before = _directorSessions.ListSessions().Count;
        var resp = await _http.PostAsJsonAsync($"directors/{DirectorId}/sessions", new NewSessionRequest
        {
            RepoPath = Path.GetTempPath(),
            Agent = "RawCli",
            Command = TestShellPath,
            Name = "stream-create-test",
            WingmanEnabled = true,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(body);
        Assert.Equal("RawCli", body.Agent);
        Assert.Equal("stream-create-test", body.Name);
        Assert.Equal(DirectorId, body.DirectorId);
        Assert.Equal(1, spy.Count); // created over the stream, not the Director's HTTP endpoint

        // A real session now exists on the Director.
        Assert.Equal(before + 1, _directorSessions.ListSessions().Count);
        Assert.True(Guid.TryParse(body.SessionId, out var sid));
        Assert.NotNull(_directorSessions.GetSession(sid));
    }

    [Fact]
    public async Task StreamModeOff_CreateEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offc-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offc", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offc");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offc",
                TailnetEndpoint = "http://127.0.0.1:59998/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync("directors/dir-offc/sessions", new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "off-create",
            });

            // No stream + unreachable Director => the HTTP create fails => 502 (today's behaviour).
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_KillEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offk-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offk", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offk");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offk",
                TailnetEndpoint = "http://127.0.0.1:59996/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.DeleteAsync($"sessions/{Guid.NewGuid()}");

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_InterruptEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offi-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-offi", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offi");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offi",
                TailnetEndpoint = "http://127.0.0.1:59995/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsync($"sessions/{Guid.NewGuid()}/interrupt", content: null);

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_PromptEndpoint_StaysOnHttp()
    {
        // A second Gateway with stream mode OFF. Its Director is registered with an UNREACHABLE endpoint,
        // so a prompt can only be attempted over HTTP - which fails - proving no stream path is used and
        // behaviour matches today's HTTP-only Gateway.
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-off-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: AllocateFreePort(), token: "t-off", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-off");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-off",
                TailnetEndpoint = "http://127.0.0.1:59994/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });

            // Unreachable Director + no stream => the HTTP location pull finds nothing => 404. Either way,
            // the request is NOT served (no stream shortcut), which is exactly today's behaviour.
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    // A dispatcher that counts how many commands the stream delivered, then runs the real shared executor.
    private sealed class CountingDispatcher
    {
        private readonly SessionManager _sessions;
        private int _count;
        public CountingDispatcher(SessionManager sessions) => _sessions = sessions;
        public int Count => _count;

        public Task<DirectorCommandResult> DispatchAsync(DirectorCommand command)
        {
            Interlocked.Increment(ref _count);
            return SessionCommandExecutor.DispatchAsync(_sessions, DirectorId, command);
        }
    }

    private GatewayStreamClient NewClient(CountingDispatcher dispatcher, TimeSpan? rePushInterval = null)
    {
        var config = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token, StreamMode = true };
        return new GatewayStreamClient(config, DirectorId, "test", SnapshotDirectorSessions, dispatcher.DispatchAsync, rePushInterval);
    }

    private List<SessionDto> SnapshotDirectorSessions() =>
        _directorSessions.ListSessions().Select(s => new SessionDto { SessionId = s.Id.ToString(), ActivityState = s.ActivityState.ToString() }).ToList();

    private static DirectorCommand PromptCommand(string sessionId, PromptRequest req) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Verb = "prompt",
        SessionId = sessionId,
        PayloadJson = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static string BufferText(Session session) => Encoding.UTF8.GetString(BufferRaw(session));

    private static byte[] BufferRaw(Session session)
    {
        if (session.Buffer is null) throw new InvalidOperationException("session has no buffer");
        return session.Buffer.DumpAll();
    }

    // Poll the proven Ping down-channel until the stream is connected and bound (Hello sent).
    private async Task WaitForStream()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await _gateway.PingDirectorAsync(DirectorId, "up") == "pong:up") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for the Director stream to connect");
    }

    // Poll the Gateway's pushed cache until the Director's snapshot carrying this session has been applied.
    private async Task WaitForPushedSession(string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.PushedSessions.TryLocate(sessionId, TimeSpan.FromSeconds(30)) is not null) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for the session to appear in the pushed cache");
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
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
