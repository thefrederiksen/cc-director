using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Audit H2, over the wire: the /session-numbers endpoints must resolve the caller's tenant from the
/// authenticated device key (server-side, never the request body) and touch only that tenant's partition.
///
/// This drives a REAL hosted GatewayHost over REAL HTTP through the REAL auth middleware, with two device
/// keys bound to two different account tenants - the same path a production Director's fleet credential
/// travels. It proves the endpoint conversion, not just the allocator: the old routes passed the bare
/// session id straight into a global pool, so two tenants using the same session-id string shared one
/// reservation and one tenant's DELETE freed the other's.
///
/// Revert-prove: drop the tenant argument at either route (allocate/release) so it uses one shared
/// partition and <see cref="Two_tenants_allocate_the_same_session_id_independently"/> reddens - the second
/// tenant's allocate returns the first's number, or its release frees the first's.
/// </summary>
public sealed class HostedSessionNumberRouteTenancyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string TenantAGuid = "55555555-5555-5555-5555-555555555555";
    private const string TenantBGuid = "66666666-6666-6666-6666-666666666666";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-snum-" + Guid.NewGuid().ToString("N"));
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

        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-unbound", "MU").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", TenantAGuid);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", TenantBGuid);
        // dev-unbound is deliberately left with no account binding.
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Two_tenants_allocate_the_same_session_id_independently()
    {
        const string sharedSession = "sess-shared";

        // Both tenants allocate a number for the SAME session-id string.
        var aNum = await AllocateAsync(_keyA, sharedSession, "dir-a");
        var bNum = await AllocateAsync(_keyB, sharedSession, "dir-b");
        Assert.NotNull(aNum);
        Assert.NotNull(bNum);

        // Each drew from its own partition, so each holds its own reservation.
        Assert.Equal(aNum, _gateway.SessionNumbers.NumberFor(new TenantId(TenantAGuid), sharedSession));
        Assert.Equal(bNum, _gateway.SessionNumbers.NumberFor(new TenantId(TenantBGuid), sharedSession));

        // Tenant A releases its identically-named session.
        var del = await Send(HttpMethod.Delete, $"session-numbers/{sharedSession}", _keyA, null);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // DESTRUCTIBILITY CONTROL - A's own reservation is gone.
        Assert.Null(_gateway.SessionNumbers.NumberFor(new TenantId(TenantAGuid), sharedSession));
        // THE PROPERTY - B's identically-named session still holds its number.
        Assert.Equal(bNum, _gateway.SessionNumbers.NumberFor(new TenantId(TenantBGuid), sharedSession));
    }

    [Fact]
    public async Task Allocate_with_no_bound_tenant_is_denied()
    {
        // A device key that authenticates but binds to no account tenant is refused on hosted (deny by
        // default), never served the Local partition.
        var resp = await Send(HttpMethod.Post, "session-numbers/allocate", _keyUnbound,
            JsonContent.Create(new SessionNumberAllocateRequest { SessionId = "sess-x", DirectorId = "dir-x" }));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<int?> AllocateAsync(string deviceKey, string sessionId, string directorId)
    {
        var resp = await Send(HttpMethod.Post, "session-numbers/allocate", deviceKey,
            JsonContent.Create(new SessionNumberAllocateRequest { SessionId = sessionId, DirectorId = directorId }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>())!.Number;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string deviceKey, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (content is not null) req.Content = content;
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
