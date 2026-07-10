using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1214: the read-only "Handover info" data path.
///
/// Director Control API GET /sessions/{sid}/handover returns the desktop "Copy Handover Info" identity
/// block (name, session id, repo, director id, machine, version) - minus the Control API endpoint, which
/// is a Director address the browser must never learn. The Gateway proxies it at the same route, gated by
/// the same Bearer/device-key auth as every other session route.
///
/// This file boots a REAL ControlApiHost for the Director-side endpoint (happy path, 404, 400) and a real
/// GatewayHost for the front-door contract (401 without a credential, 404 when no Director owns the id).
/// </summary>
public sealed class HandoverInfoControlApiTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "9.9.9-handover-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
    }

    [Fact]
    public async Task Handover_ReturnsIdentityBlock_ForAKnownSession()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        var dto = await _client.GetFromJsonAsync<HandoverInfoDto>($"sessions/{sid}/handover");

        Assert.NotNull(dto);
        Assert.Equal(sid, dto!.SessionId);
        Assert.Equal("9.9.9-handover-test", dto.Version);
        Assert.Equal(Environment.MachineName, dto.MachineName);
        Assert.Equal(@"C:\test\handover-info-test", dto.RepoPath);
        Assert.False(string.IsNullOrWhiteSpace(dto.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(dto.DirectorId));
    }

    [Fact]
    public async Task Handover_NeverLeaksAControlApiEndpoint()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        // The raw JSON must carry no Director address - only the identity fields.
        var raw = await _client.GetStringAsync($"sessions/{sid}/handover");
        Assert.DoesNotContain("controlEndpoint", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlApi", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handover_UnknownSession_Returns404()
    {
        var resp = await _client.GetAsync($"sessions/{Guid.NewGuid()}/handover");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Handover_InvalidSessionId_Returns400()
    {
        var resp = await _client.GetAsync("sessions/not-a-guid/handover");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static Session MakeIdleSession()
    {
        var backend = new StubBackend();
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\handover-info-test",
            workingDirectory: @"C:\test\handover-info-test",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "handover-info-test",
            customColor: null);
        session.MarkRunning();
        return session;
    }

    private sealed class StubBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}

/// <summary>
/// Gateway front-door contract for GET /sessions/{sid}/handover (issue #1214). Boots a real GatewayHost
/// with auth ON and no Director present, so the credential gate and the session-lookup 404 are both
/// observable without a Director on the other side.
/// </summary>
public sealed class HandoverInfoGatewayTests : IAsyncLifetime
{
    private const string GatewayToken = "handover-test-token";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ccd-handover-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-handover-instances-" + Guid.NewGuid().ToString("N"));
    private string? _prevRoot;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _gateway = new GatewayHost(port: AllocateFreePort(), token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Handover_WithoutAToken_Returns401()
    {
        // No Authorization header: the global gate must reject before any Director lookup.
        var resp = await _http.GetAsync($"sessions/{Guid.NewGuid()}/handover");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Handover_WithAValidToken_ButNoOwningDirector_Returns404()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"sessions/{Guid.NewGuid()}/handover");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
