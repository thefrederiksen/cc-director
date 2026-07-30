using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Destructive-call gate on DELETE /directors/{id} (issue #212 W6/L4): a shutdown must
/// state a reason, and when the Director has live sessions the request must confirm
/// their count - otherwise 409 with the live-session list, and the graceful shutdown is
/// never triggered. Born from the 2026-06-06 post-mortem, where the force-kill path could
/// take down a Director plus 10 live sessions without a trace.
///
/// Gateway Cleanup mission (the cut): the live-session count is now read from the PUSH store
/// (a Director's sessions arrive by PushSnapshot, never an HTTP pull), and the graceful stop is
/// the new director-level "shutdown" tunnel verb. Each Director registers UNREACHABLE and answers
/// the shutdown verb over the stream, so a working stop proves the tunnel by construction. An
/// UNREACHABLE Director is one with NO fresh push and no stream: the session gate is skipped and
/// the shutdown verb returns null (not connected) -> 502 (or the force-kill path).
/// </summary>
public sealed class DirectorShutdownGateTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<Gate> _fakes = new();
    // One per RegisterUnreachable call, held to the end of the class: an endpoint registered as
    // unreachable must STAY unreachable for the whole test, so the port is reserved rather than probed and
    // released (issue #1156).
    private readonly List<DeadPortReservation> _deadEndpoints = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
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
        foreach (var f in _fakes) await f.DisposeAsync();
        foreach (var reservation in _deadEndpoints) reservation.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---------- reason required ----------

    [Fact]
    public async Task Delete_without_reason_returns_400_and_never_calls_shutdown()
    {
        var fake = await StartConnected(live: 0);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest());

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(fake.ShutdownCalled, "the shutdown verb must not fire when the reason is missing");
    }

    [Fact]
    public async Task Delete_with_blank_reason_returns_400()
    {
        var fake = await StartConnected(live: 0);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(fake.ShutdownCalled);
    }

    // ---------- live-session gate ----------

    [Fact]
    public async Task Delete_with_live_sessions_and_no_confirm_returns_409_with_session_list()
    {
        var fake = await StartConnected(live: 3);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "test stop" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(fake.ShutdownCalled, "the shutdown verb must not fire when the session gate blocks");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("liveSessionCount").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task Delete_with_wrong_confirm_count_returns_409()
    {
        var fake = await StartConnected(live: 3);

        var resp = await DeleteDirector(fake.DirectorId,
            new ShutdownDirectorRequest { Reason = "test stop", ConfirmSessions = 2 });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(fake.ShutdownCalled);
    }

    [Fact]
    public async Task Delete_with_matching_confirm_calls_graceful_shutdown()
    {
        var fake = await StartConnected(live: 3);

        var resp = await DeleteDirector(fake.DirectorId,
            new ShutdownDirectorRequest { Reason = "test stop", ConfirmSessions = 3 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled, "matching confirmSessions must let the graceful shutdown verb through");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Delete_with_zero_live_sessions_needs_only_a_reason()
    {
        var fake = await StartConnected(live: 0);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "idle teardown" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled);
    }

    [Fact]
    public async Task Exited_sessions_do_not_count_toward_the_gate()
    {
        var fake = await StartConnected(live: 0, exited: 2);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "idle teardown" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled);
    }

    // ---------- unreachable Director (not tunnel-connected) ----------

    [Fact]
    public async Task Unreachable_director_skips_gate_and_returns_502_without_force()
    {
        // Registered but never tunnel-connected: the live count is unknowable (gate skipped) and the
        // shutdown verb cannot be delivered -> 502, never a silent "nothing happened".
        var id = RegisterUnreachable();

        var resp = await DeleteDirector(id, new ShutdownDirectorRequest { Reason = "hung director" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Unreachable_director_with_force_attempts_kill_and_reports_failure_for_dead_pid()
    {
        // Registered with a PID that cannot exist, so the force path runs and reports its failure
        // instead of silently doing nothing.
        var id = RegisterUnreachable(pid: int.MaxValue - 1);

        var resp = await DeleteDirector(id,
            new ShutdownDirectorRequest { Reason = "hung director", Force = true });

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("could not kill process", body);
    }

    [Fact]
    public async Task Delete_without_body_returns_404_for_unknown_director()
    {
        // A body-less DELETE (no Content-Type at all) must still route to the handler and 404 on
        // an unknown id, not bounce off content negotiation.
        var resp = await _http.DeleteAsync($"directors/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> DeleteDirector(string id, ShutdownDirectorRequest body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{id}")
        {
            Content = JsonContent.Create(body),
        };
        return await _http.SendAsync(req);
    }

    // Register a Director UNREACHABLE - in the registry but with no tunnel connection and no push.
    private string RegisterUnreachable(int pid = 1)
    {
        var id = Guid.NewGuid().ToString();
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = ReserveDeadEndpoint(),
            Pid = pid,
            MachineName = "GATE_TEST",
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        return id;
    }

    // A tunnel-connected Director that pushes its live/exited sessions into the roster and answers the
    // shutdown verb. Because it is registered UNREACHABLE, a working shutdown proves the tunnel.
    private async Task<Gate> StartConnected(int live, int exited = 0)
    {
        var gate = new Gate();
        var id = "dir-" + Guid.NewGuid().ToString("N")[..8];
        gate.Director = await FakeTunnelDirector.StartAsync(_gateway, Token, id, dispatch: gate.Dispatch);
        _fakes.Add(gate);

        var sessions = new List<SessionDto>();
        for (var i = 0; i < live; i++)
            sessions.Add(new SessionDto
            {
                SessionId = $"live-{i}", Name = $"live session {i}", Agent = "ClaudeCode",
                RepoPath = "/repo", Status = "Running", ActivityState = "Working", StatusColor = "blue",
            });
        for (var i = 0; i < exited; i++)
            sessions.Add(new SessionDto
            {
                SessionId = $"dead-{i}", Name = $"dead session {i}", Agent = "ClaudeCode",
                RepoPath = "/repo", Status = "Exited", ActivityState = "Exited", StatusColor = "unknown",
            });

        await gate.Director.PushSnapshotAsync(sessions.ToArray());
        return gate;
    }

    // A loopback URL nothing will ever answer on, reserved for the lifetime of this test class.
    private string ReserveDeadEndpoint()
    {
        var reservation = DeadPortReservation.Reserve();
        _deadEndpoints.Add(reservation);
        return reservation.LoopbackUrl;
    }

    // Records whether the graceful "shutdown" verb was delivered over the tunnel.
    private sealed class Gate : IAsyncDisposable
    {
        public FakeTunnelDirector Director = null!;
        public string DirectorId => Director.DirectorId;
        public bool ShutdownCalled { get; private set; }

        public DirectorCommandResult Dispatch(DirectorCommand cmd)
        {
            if (cmd.Verb == "shutdown")
            {
                ShutdownCalled = true;
                return DirectorCommandResult.Success();
            }
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        }

        public ValueTask DisposeAsync() => Director is null ? ValueTask.CompletedTask : Director.DisposeAsync();
    }
}
