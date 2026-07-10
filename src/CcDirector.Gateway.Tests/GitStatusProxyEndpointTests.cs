using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1266: the Gateway's READ-ONLY source-control proxy GET /sessions/{sid}/git. These wire tests
/// boot a real Gateway with the host-wide auth middleware OFF, so the route's OWN token self-check is the
/// only gate - proving the two things the issue calls out: (1) it forwards the owning Director's snapshot
/// (including the additive per-file lists) and (2) it authenticates with a PER-DEVICE key, not only the
/// shared machine token (the device-blind 401 that once bit the dictation route, issue #1045), and stays
/// gated with no credential even though the global gate is off.
/// </summary>
public sealed class GitStatusProxyEndpointTests : IAsyncLifetime
{
    private const string MachineToken = "machine-token-1266";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private GitStubDirector _director = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Auth OFF: the host-wide gate does not run, so the ONLY thing gating /git is the route's own
        // HasValidToken self-check - exactly what these tests exercise.
        _gateway = new GatewayHost(port: FreePort(), token: MachineToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        // No default Authorization header - each test sets exactly the credential it means to test.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_director is not null) await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    private async Task RegisterDirectorAsync()
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4321,
            MachineName = Environment.MachineName, // same-machine loopback stub
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        // Registration itself is a machine-token call.
        using var msg = new HttpRequestMessage(HttpMethod.Post, "directors/register")
        {
            Content = JsonContent.Create(req),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MachineToken);
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<HttpResponseMessage> GetGitAsync(string sid, string? bearer)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"sessions/{sid}/git");
        if (bearer is not null)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(msg);
    }

    [Fact]
    public async Task Machine_token_forwards_the_directors_snapshot_with_per_file_lists()
    {
        _director = new GitStubDirector("git-session-1");
        await _director.StartAsync();
        await RegisterDirectorAsync();

        var resp = await GetGitAsync("git-session-1", MachineToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = await resp.Content.ReadFromJsonAsync<GitSnapshot>();
        Assert.NotNull(snap);
        Assert.Equal("main", snap!.Branch);
        Assert.Equal("ok", snap.Status);
        var staged = Assert.Single(snap.StagedChanges);
        Assert.Equal("src/Added.cs", staged.Path);
        Assert.Equal("A", staged.ChangeKind);
        var unstaged = Assert.Single(snap.UnstagedChanges);
        Assert.Equal("src/Modified.cs", unstaged.Path);
        Assert.Equal("M", unstaged.ChangeKind);
    }

    [Fact]
    public async Task A_per_device_key_is_accepted_and_forwards()
    {
        // The exact requirement: a phone/browser per-device key (not the shared machine token) must be
        // accepted by the route's own auth check.
        var deviceKey = _gateway.Devices.Register("browser-1266", "Chrome on Windows", "browser", "browser").DeviceKey;

        _director = new GitStubDirector("git-session-2");
        await _director.StartAsync();
        await RegisterDirectorAsync();

        var resp = await GetGitAsync("git-session-2", deviceKey);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = await resp.Content.ReadFromJsonAsync<GitSnapshot>();
        Assert.Equal("main", snap!.Branch);
    }

    [Fact]
    public async Task No_credential_is_rejected_even_with_the_host_gate_off()
    {
        var resp = await GetGitAsync("git-session-3", bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task An_unknown_bearer_is_rejected()
    {
        var resp = await GetGitAsync("git-session-4", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>A minimal stub Director (no token gate) that resolves ownership at GET /sessions/{sid}
    /// and answers GET /sessions/{sid}/git with an enriched snapshot. Kept open so these tests exercise
    /// only the GATEWAY's auth + forward, not the Director-side auth (covered elsewhere).</summary>
    private sealed class GitStubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";

        private readonly string _sessionId;
        private WebApplication? _app;

        public GitStubDirector(string sessionId) => _sessionId = sessionId;

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "GitStubDirector" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();

            _app.MapGet("/sessions/{sid}", (string sid) =>
                sid == _sessionId
                    ? Results.Json(new SessionDto { SessionId = _sessionId, Agent = "ClaudeCode", ActivityState = "Idle", StatusColor = "green" })
                    : Results.NotFound());

            _app.MapGet("/sessions/{sid}/git", () => Results.Json(new GitSnapshot
            {
                Branch = "main",
                Dirty = true,
                Status = "ok",
                LastCommit = "a1b2c3d seed",
                StagedChanges = { new GitChangeEntry { Path = "src/Added.cs", ChangeKind = "A" } },
                UnstagedChanges = { new GitChangeEntry { Path = "src/Modified.cs", ChangeKind = "M" } },
            }));

            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
