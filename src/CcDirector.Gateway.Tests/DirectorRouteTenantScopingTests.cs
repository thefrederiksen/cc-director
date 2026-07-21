using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// MTR-01: cross-tenant RCE / read / control via BARE director ids, proven over the REAL mapped endpoints and
/// the REAL tunnel.
///
/// Before this fix ~20 <c>/directors/{id}/*</c> routes and the <c>/interrupted</c> plane resolved a Director by
/// a bare id that scanned EVERY tenant's entries and returned the freshest match. So tenant A, holding its own
/// valid device key, could enumerate tenant B's director id and then <c>POST /directors/{id}/sessions</c> to
/// spawn a coding agent on B's machine, read B's repos/handovers/filesystem, overwrite B's settings, or stop
/// B's Director. The fix deletes the bare-id registry accessor and forces every per-director route through the
/// tenant-scoped <c>DirectorRegistry.Get(tenant, id)</c>, resolved from the request's authenticated device key.
///
/// The sharp proof is the SAME director id under TWO tenants: <c>dir-shared</c> exists in both tenant-alice and
/// tenant-bob. A caller naming it can only ever reach ITS OWN "dir-shared", never the other tenant's - the id is
/// identical, only the authenticated tenant differs. A "no command reached the other tenant" assertion is made
/// directly: each fake Director records every verb the Gateway sends it, and B's Director records NOTHING for
/// A's requests.
///
/// Revert-prove: restore the bare <c>DirectorRegistry.Get(string)</c> overload and point the routes back at it,
/// and A's spawn lands on (or at minimum reaches) B's Director / the foreign-id read returns 200 - so the
/// "DoesNotContain create/repos-list on B" and the 404 assertions go RED.
///
/// This drives the REAL auth middleware (which stashes the authenticated device key) and the REAL tunnel Hello
/// (which binds each Director's tenant), like <c>SessionServingReadIsolationTests</c>. The assembly runs
/// sequentially, so toggling CC_GATEWAY_HOSTED here is safe; it is reset in DisposeAsync.
/// </summary>
public sealed class DirectorRouteTenantScopingTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    // Two tenants, one shared director id: dir-shared exists in BOTH alice and bob.
    private FakeTunnelDirector _dirAShared = null!; // tenant-alice, id "dir-shared"
    private FakeTunnelDirector _dirBShared = null!; // tenant-bob,   id "dir-shared" (SAME id)
    private FakeTunnelDirector _dirBOnly = null!;   // tenant-bob,   id "dir-b-only" (only bob has it)

    // Every verb the Gateway sends each Director over the tunnel, so "no command reached the other tenant" is a
    // direct assertion, not an inference from a status code.
    private readonly ConcurrentQueue<string> _aSharedVerbs = new();
    private readonly ConcurrentQueue<string> _bSharedVerbs = new();
    private readonly ConcurrentQueue<string> _bOnlyVerbs = new();

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-mtr01-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two device keys, each bound to its OWN tenant, plus one registered-but-unbound key.
        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", "tenant-bob");

        // Each Director authenticates with its OWN device key -> the tunnel Hello binds its tenant -> its entry
        // lands in that tenant's partition. dir-shared is registered under BOTH tenants (the sharp case).
        _dirAShared = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-shared", "MA", dispatch: Recorder(_aSharedVerbs));
        _dirBShared = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-shared", "MB", dispatch: Recorder(_bSharedVerbs));
        _dirBOnly = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b-only", "MB", dispatch: Recorder(_bOnlyVerbs));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirAShared.DisposeAsync();
        await _dirBShared.DisposeAsync();
        await _dirBOnly.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Spawn_on_a_shared_director_id_reaches_only_the_callers_own_director()
    {
        // The RCE vector: POST /directors/{id}/sessions spawns a coding agent on that Director's machine. A and
        // B both hold a Director with the IDENTICAL id "dir-shared"; A's spawn must land on A's OWN Director and
        // never on B's. This is the whole exploit closed: same id, different tenant, no crossing.
        var resp = await Post("directors/dir-shared/sessions", _keyA,
            new NewSessionRequest { RepoPath = "/repo", Agent = "claude" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Contains("create", _aSharedVerbs);       // A's own Director ran the spawn
        Assert.DoesNotContain("create", _bSharedVerbs);  // B's same-id Director NEVER saw the command
    }

    [Fact]
    public async Task A_foreign_director_id_is_404_and_no_command_is_sent()
    {
        // An id that exists ONLY in tenant B. Tenant A naming it is refused AT the registry gate (404), and B's
        // Director is never reached over the tunnel.
        var crossTenant = await Get("directors/dir-b-only/repos", _keyA);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.DoesNotContain("repos-list", _bOnlyVerbs);

        // Positive control: B's OWN key reaches its Director, proving the id is real and routable - so the 404
        // above is the tenant gate, not a missing Director.
        var sameTenant = await Get("directors/dir-b-only/repos", _keyB);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Contains("repos-list", _bOnlyVerbs);
    }

    [Fact]
    public async Task A_write_route_on_a_foreign_director_id_is_404_and_spawns_nothing()
    {
        // The same guarantee on the WRITE/RCE path: A cannot spawn on an id it does not own, and B's Director
        // gets no create command.
        var resp = await Post("directors/dir-b-only/sessions", _keyA,
            new NewSessionRequest { RepoPath = "/repo", Agent = "claude" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("create", _bOnlyVerbs);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default: an authenticated but tenant-unbound key never falls back to the Local partition on a
        // per-director route - read OR write.
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("directors/dir-shared/repos", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post("directors/dir-shared/sessions", _keyUnbound, new NewSessionRequest { RepoPath = "/repo" })).StatusCode);
    }

    [Fact]
    public async Task Interrupted_plane_fans_out_only_to_the_callers_own_directors()
    {
        // GET /interrupted used the fleet-global director list, so it enumerated and queried every tenant's
        // Directors. Scoped to the caller's tenant, A's request reaches only A's Directors' crash journals; B's
        // Directors are never queried over the tunnel.
        var resp = await Get("interrupted", _keyA);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains("interrupted-list", _aSharedVerbs);        // A's own Director was queried
        Assert.DoesNotContain("interrupted-list", _bSharedVerbs);  // B's same-id Director never was
        Assert.DoesNotContain("interrupted-list", _bOnlyVerbs);    // nor B's other Director
    }

    // A dispatch that RECORDS every verb the Gateway sends this Director, then answers the read/create verbs
    // this test drives with an OK body so a route that DID reach it returns success (the positive controls).
    // The point is the verb log, not the body.
    private static Func<DirectorCommand, DirectorCommandResult> Recorder(ConcurrentQueue<string> log) =>
        cmd =>
        {
            log.Enqueue(cmd.Verb);
            return cmd.Verb switch
            {
                "create" => FakeTunnelDirector.Ok(new SessionDto
                {
                    SessionId = "new-sess",
                    Agent = "claude",
                    RepoPath = "/repo",
                    Status = "Running",
                    StatusColor = "blue",
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow,
                }),
                "repos-list" => FakeTunnelDirector.Ok(Array.Empty<RepositoryDto>()),
                "interrupted-list" => FakeTunnelDirector.Ok(Array.Empty<CrashJournalDto>()),
                _ => FakeTunnelDirector.Ok(new { ok = true }),
            };
        };

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> Post(string path, string deviceKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
