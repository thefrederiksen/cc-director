using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (session-serving PR1): the cockpit READ path is tenant-aware end-to-end over real HTTP.
/// Two Directors connect on two DIFFERENT tenants, each authenticated with its OWN per-device key; the Gateway
/// resolves the request's tenant from that same authenticated key and serves ONLY that tenant's sessions:
///   - GET /sessions with device key A returns ONLY A's sessions, never B's;
///   - GET /sessions/{sid} for B's session, read with key A, is 404 (never located cross-tenant), while B's
///     own key reads it 200;
///   - a per-session tunnel leg (the WS-proxy screenshot list) for B's session, read with key A, is 503 (owner
///     not locatable under A's tenant), while B's own key reaches its Director over the tunnel;
///   - a request whose authenticated device key has NO bound tenant is DENIED 403 (deny-by-default), never
///     served the Local partition.
///
/// Revert-prove: change ResolveReadTenant (GatewayEndpoints) / ResolveTenantOrDeny (SessionWsProxyEndpoints)
/// back to TenantId.Local and key A reads the empty Local partition, so "A sees sess-a" and "A reaches its own
/// session" go RED (and the 403 deny collapses to an empty 200).
///
/// This drives the REAL mapped endpoints through the REAL auth middleware (which stashes the authenticated
/// device key) and the REAL tunnel Hello (which binds each Director's tenant) - it is the wired read path the
/// unit-level boundary tests (HostedTenancyActivationTests) do not exercise. The assembly runs sequentially
/// (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is reset in DisposeAsync.
/// </summary>
public sealed class SessionServingReadIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "sess-a";
    private const string SessB = "sess-b";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ss-" + Guid.NewGuid().ToString("N"));
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
        // Binding directly (as the hosted hub isolation test does) - the read path partitions by the bound
        // TenantId; no tenants-table mint sits on the read path.
        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", "tenant-bob");

        // Each Director authenticates with its OWN device key -> the tunnel Hello binds its tenant -> its pushed
        // session lands in that tenant's partition. dir-B answers the screenshots-list verb so the same-tenant
        // WS-proxy leg proves it reaches its Director (the cross-tenant read is the 503 we assert on).
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA");
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB",
            dispatch: cmd => cmd.Verb == "screenshots-list"
                ? FakeTunnelDirector.Ok(new { items = Array.Empty<object>() })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"));
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Sessions_roster_serves_only_the_requesting_tenants_sessions()
    {
        var seenByA = await SessionIds(_keyA);
        var seenByB = await SessionIds(_keyB);

        Assert.Contains(SessA, seenByA);
        Assert.DoesNotContain(SessB, seenByA);

        Assert.Contains(SessB, seenByB);
        Assert.DoesNotContain(SessA, seenByB);
    }

    [Fact]
    public async Task Session_by_id_is_isolated_across_tenants()
    {
        // B's own key reads B's session; A's key cannot see B's session at all.
        Assert.Equal(HttpStatusCode.OK, (await Get($"sessions/{SessB}", _keyB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"sessions/{SessB}", _keyA)).StatusCode);

        // ...and symmetrically for A's session.
        Assert.Equal(HttpStatusCode.OK, (await Get($"sessions/{SessA}", _keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"sessions/{SessA}", _keyB)).StatusCode);
    }

    [Fact]
    public async Task WsProxy_leg_locate_is_tenant_scoped()
    {
        // The WS-proxy per-session legs resolve the owning Director from the pushed store under the REQUEST's
        // tenant. PR1 makes that LOCATE tenant-scoped; the relay that follows a successful locate lands in a
        // later slice, so this proves the locate line only - by which 503 body each request gets:
        //
        //  - A's key asking for B's session is NOT locatable under A's tenant, so it is rejected AT the locate
        //    with the honest "not connected right now ... will reconnect" reason (RejectAsync).
        //  - B's own key DOES locate B's session (tenant match), so it passes the locate and reaches the relay,
        //    which - until the relay slice - answers with the different "owning director is not connected" body.
        //    The point is that B's request never hits the not-locatable reject; the locate resolved its tenant.
        //
        // Revert-prove: change ResolveTenantOrDeny back to TenantId.Local and B's key locates nothing either
        // (B's session lives under tenant-bob, not Local), so B's request ALSO gets the "will reconnect" reject
        // -> the DoesNotContain below goes RED.
        var crossTenant = await Get($"sessions/{SessB}/screenshots?count=1", _keyA);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, crossTenant.StatusCode);
        Assert.Contains("reconnect", await crossTenant.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var sameTenant = await Get($"sessions/{SessB}/screenshots?count=1", _keyB);
        Assert.DoesNotContain("reconnect", await sameTenant.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default: an authenticated but tenant-unbound key never falls back to the Local partition.
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("sessions", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get($"sessions/{SessA}", _keyUnbound)).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<string[]> SessionIds(string deviceKey)
    {
        var resp = await Get("sessions", deviceKey);
        resp.EnsureSuccessStatusCode();
        var sessions = await resp.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts) ?? new();
        return sessions.Select(s => s.SessionId!).ToArray();
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Working",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
