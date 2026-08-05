using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the launcher registration surface:
///   POST /launchers/register
///   POST /launchers/{machine}/heartbeat
///   DELETE /launchers/{machine}
///   GET  /launchers
///
/// And the machine relay routes:
///   POST /machines/{machine}/director/restart|start|stop
///   POST /machines/{machine}/launch
///
/// Issue #331. Phase 6 of the remove-the-network-port mission: registration is presence-and-identity
/// only (machine, pid, version - no port, no token, no network address), and a relay to a machine
/// whose launcher is registered but not STREAM-CONNECTED is a loud 502 refusal, never a dial - in
/// these tests no launcher ever joins the stream, so every guarded route that passes its guard ends
/// in exactly that refusal, which is itself the proof the guard was what answered.
/// </summary>
public sealed class LauncherRegistryEndpointTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-launcher-test-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // AC1: Launcher registration (POST /launchers/register)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Register_Launcher_Returns201AndAppearsInList()
    {
        var req = BuildRegistrationRequest("MACHINE-A", pid: 4211);

        var resp = await _http.PostAsJsonAsync("launchers/register", req);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.NotNull(list);
        var entry = Assert.Single(list!, l => l.MachineName.Equals("MACHINE-A", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4211, entry.Pid);
        Assert.Equal("1.0.0", entry.Version);
    }

    [Fact]
    public async Task Register_Launcher_IsIdempotent()
    {
        var req = BuildRegistrationRequest("MACHINE-B", version: "1.0.0");
        await _http.PostAsJsonAsync("launchers/register", req);

        var req2 = BuildRegistrationRequest("MACHINE-B", version: "1.0.1");
        var resp = await _http.PostAsJsonAsync("launchers/register", req2);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        var entries = list!.Where(l => l.MachineName.Equals("MACHINE-B", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(entries);
        Assert.Equal("1.0.1", entries[0].Version);
    }

    [Fact]
    public async Task Register_Launcher_RejectsMissingMachineName()
    {
        var req = new LauncherRegistrationRequest { MachineName = "" };
        var resp = await _http.PostAsJsonAsync("launchers/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // The listing must never serve a dial-back surface: no port, no token, no network address. This is
    // the route-level twin of the registry's own shape pin - a stored address for a listener that no
    // longer exists is exactly what a future second door would be built on.
    [Fact]
    public async Task Register_Launcher_ListingCarriesNoDialBackSurface()
    {
        await _http.PostAsJsonAsync("launchers/register", BuildRegistrationRequest("SHAPE-MACHINE"));

        var json = await _http.GetStringAsync("launchers");

        Assert.DoesNotContain("port", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("networkAddress", json, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_KnownLauncher_Returns200()
    {
        var req = BuildRegistrationRequest("MACHINE-HB");
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsync("launchers/MACHINE-HB/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_UnknownLauncher_Returns410()
    {
        var resp = await _http.PostAsync("launchers/NONEXISTENT/heartbeat", null);
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Unregister
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unregister_RemovesLauncherFromList()
    {
        var req = BuildRegistrationRequest("MACHINE-DEL");
        await _http.PostAsJsonAsync("launchers/register", req);

        var beforeCount = (await _http.GetFromJsonAsync<List<LauncherDto>>("launchers"))!.Count;
        Assert.True(beforeCount >= 1);

        var delResp = await _http.DeleteAsync("launchers/MACHINE-DEL");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.DoesNotContain(list!, l => l.MachineName.Equals("MACHINE-DEL", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // AC3: Slot guard (relay refuses main + slots 1-4 without confirmProtected)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_SlotGuard_RefusesMainBuildWithoutConfirm()
    {
        var req = BuildRegistrationRequest("GUARD-MACHINE");
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\Program Files\cc-director\cc-director.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE/director/restart", body);

        // 403 = slot guard fired.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_RefusesSlot1WithoutConfirm()
    {
        var req = BuildRegistrationRequest("GUARD-MACHINE2");
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\cc-director\local_builds\cc-director1.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE2/director/stop", body);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_AllowsSlot5WithoutConfirm()
    {
        // Slot 5 (agent slot) is NOT protected. No launcher stream is joined in these tests, so the
        // guard passing shows up as the not-connected 502 - not as a 403 from the guard.
        var req = BuildRegistrationRequest("GUARD-MACHINE3");
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\cc-director\local_builds\cc-director5.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE3/director/restart", body);

        // 502 = guard passed, but the launcher holds no stream connection.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_AllowsMainBuildWithConfirmProtected()
    {
        // With confirmProtected=true the guard is bypassed. Expect the not-connected 502 (not 403).
        var req = BuildRegistrationRequest("GUARD-MACHINE4");
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\Program Files\cc-director\cc-director.exe", confirmProtected = true };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE4/director/restart", body);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC5: 404 when launcher not registered
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_UnknownMachine_Returns404()
    {
        var resp = await _http.PostAsync("machines/UNKNOWN-MACHINE/director/restart", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("UNKNOWN-MACHINE", json);
    }

    [Fact]
    public async Task Relay_Launch_UnknownMachine_Returns404()
    {
        var resp = await _http.PostAsJsonAsync("machines/UNKNOWN-MACHINE/launch", new { path = "foo.exe", confirmProtected = true });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tenant-boundary hardening (release 2026-07-31, finding CR-5): EVERY launch
    // requires confirmProtected=true, the same flag the restart/stop slot guard
    // reads. Key possession alone used to be enough to start a program.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_Launch_WithoutConfirmProtected_IsRefused403_BeforeAnyDelivery()
    {
        // A REGISTERED machine with no stream: if delivery were attempted the answer would be the
        // not-connected 502. The 403 with the launch_guard error proves the refusal happened BEFORE
        // any delivery - the reported CR-5 symptom was that this request relayed on key possession
        // alone.
        var req = BuildRegistrationRequest("LAUNCH-GUARD-MACHINE");
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsJsonAsync("machines/LAUNCH-GUARD-MACHINE/launch",
            new { path = @"C:\Windows\System32\cmd.exe", args = "/c whoami" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("launch_guard", json);
        Assert.Contains("confirmProtected", json);
    }

    [Fact]
    public async Task Relay_Launch_WithConfirmProtected_PassesTheGateAndReachesTheDispatch()
    {
        // The allowed-path control: with the confirmation present the gate opens and delivery is
        // attempted - no stream is joined, so that attempt is the not-connected 502, which is exactly
        // the proof that the request got PAST the 403 gate.
        var req = BuildRegistrationRequest("LAUNCH-CONFIRM-MACHINE");
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsJsonAsync("machines/LAUNCH-CONFIRM-MACHINE/launch",
            new { path = @"C:\Windows\System32\cmd.exe", confirmProtected = true });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC5: launcher registered but not stream-connected -> loud 502, never a dial
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_RegisteredButNotConnected_Returns502ThatSaysSo()
    {
        var req = BuildRegistrationRequest("OFFLINE-MACHINE");
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsync("machines/OFFLINE-MACHINE/director/restart", null);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        // The message must say what is actually wrong - the stream connection - and must not name an
        // address or port, because there is nothing to dial.
        Assert.Contains("not connected", json);
        Assert.DoesNotContain("127.0.0.1", json);
    }

    // -------------------------------------------------------------------------
    // AC2: GET /launchers lists the registered machines
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetLaunchers_ReturnsAllRegistered()
    {
        await _http.PostAsJsonAsync("launchers/register", BuildRegistrationRequest("LIST-A"));
        await _http.PostAsJsonAsync("launchers/register", BuildRegistrationRequest("LIST-B"));

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.NotNull(list);
        Assert.Contains(list!, l => l.MachineName.Equals("LIST-A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list!, l => l.MachineName.Equals("LIST-B", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Auth (AC5: wrong token -> 401)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Register_Launcher_WrongToken_Returns401()
    {
        using var badHttp = new HttpClient { BaseAddress = _http.BaseAddress };
        badHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "WRONG");

        var resp = await badHttp.PostAsJsonAsync("launchers/register",
            BuildRegistrationRequest("TOKEN-TEST"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LauncherRegistrationRequest BuildRegistrationRequest(
        string machine, int pid = 12345, string version = "1.0.0") =>
        new()
        {
            MachineName = machine,
            Pid = pid,
            Version = version,
            StartedAt = DateTime.UtcNow,
        };

}
