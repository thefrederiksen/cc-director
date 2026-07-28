using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STANDARD THIS FILE ENFORCES, IN BOTH DIRECTIONS AT ONCE.
///
/// A tenant has FULL control of every device registered to its Gateway - hosted or self-hosted, no difference -
/// including starting and stopping sessions and the launcher on any of them, and including when an agent does
/// it on the owner's behalf. AND one subscriber must never see, start, stop or enumerate another subscriber's
/// machines. Those are not in tension; they are the same requirement stated from each side, and a change that
/// satisfies only one of them is a defect either way.
///
/// WHAT THIS REPLACED, AND WHY THE REPLACEMENT IS NOT A WEAKENING. The launcher and machine families used to be
/// DENIED OUTRIGHT on hosted (issue #1917) and this file used to assert that refusal verbatim. The leak the
/// deny closed was real - the family was tenant-blind by construction, so any authenticated device key could
/// enumerate every tenant's machines through GET /launchers and then run code on them through
/// POST /machines/{machine}/launch. But the deny closed it by removing cross-machine control from every hosted
/// tenant ON THEIR OWN MACHINES, which is the product's defining capability. The deny's own un-deny
/// instruction named the correct close: bind machine identity to a tenant at registration and authorize every
/// call against the calling tenant. That is now in place, so the assertions moved from "nobody may" to
/// "its owner may, and nobody else can" - which is a STRICTLY STRONGER statement about isolation, because a
/// refusal that answers everyone identically cannot distinguish a working partition from a broken one.
///
/// EVERY CROSS-TENANT PROOF HERE OBSERVES THE ACT, NOT ONLY THE STATUS CODE. A 404 to Bob would also be
/// produced by a Gateway that was simply broken. So each cross-tenant test additionally asserts the thing the
/// leak would have caused: Alice's stub launcher process is NOT dialed, Alice's stored port and token are NOT
/// overwritten, Alice's registry row is NOT removed, and the spawner is NOT entered on her behalf. The
/// same-tenant tests are the control that proves those instruments are live - the same stub launcher IS dialed
/// and DOES return its sentinel when its own owner asks.
///
/// TWO REAL TENANTS ON A REAL HOSTED GATEWAY. Alice and Bob are minted through the real
/// <c>TenantRegistry</c> and hold real enrolled device keys bound through the real <c>DeviceRegistry</c>, so
/// the tenant each request resolves to comes from the authenticated credential exactly as it does in
/// production. Nothing here stubs the tenant boundary.
/// </summary>
public sealed class HostedTenantMachineControlTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The machine Alice owns. Bob names this SAME string in every cross-tenant attempt, because the
    /// defect being closed is precisely that a bare machine name used to be enough to reach it.</summary>
    private const string AliceMachine = "ALICE-PC";

    private const string AliceLauncherToken = "alice-launcher-token";
    private const int AliceLauncherPid = 4242;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _aliceKey = "";
    private string _bobKey = "";
    private string _unboundKey = "";
    private TenantId _aliceTenant;
    private TenantId _bobTenant;

    // A stub standing in for the real cc-launcher REST API on Alice's machine. Every dial it receives is
    // recorded, so "Bob reached Alice's launcher" is a DIRECT assertion rather than an inference from a status
    // code. If the isolation ever regressed, THIS is what would be executing Bob's commands.
    private WebApplication? _aliceLauncher;
    private int _aliceLauncherPort;
    private readonly List<string> _aliceLauncherHits = new();

    private string? _priorHosted;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-tenant-machines-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // EXPLICIT, not ambient: this class asserts HOSTED behaviour, so it states hosted mode itself rather
        // than inheriting whatever the runner happened to leave set, and proves the statement took.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        _aliceLauncher = await StartStubLauncherAsync();

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two fully enrolled, tenant-bound device keys - one per account.
        _aliceKey = _gateway.Devices.Register("dev-tenantmachines-alice", "ALICE-PC").DeviceKey;
        _aliceTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-tenantmachines-alice", "tenantmachines-alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-tenantmachines-alice", "sub-tenantmachines-alice", _aliceTenant.Value);

        _bobKey = _gateway.Devices.Register("dev-tenantmachines-bob", "BOB-PC").DeviceKey;
        _bobTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-tenantmachines-bob", "tenantmachines-bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-tenantmachines-bob", "sub-tenantmachines-bob", _bobTenant.Value);

        Assert.NotEqual(_aliceTenant.Value, _bobTenant.Value);

        // A device that is enrolled but bound to NO account. On hosted it resolves to no tenant, which is a
        // deny-by-default 403 everywhere in this family - never a fall back to the Local or System partition.
        _unboundKey = _gateway.Devices.Register("dev-tenantmachines-unbound", "NOBODY-PC").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        if (_aliceLauncher is not null) await _aliceLauncher.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>The stub cc-launcher on Alice's machine. It answers every lifecycle verb and the generic launch
    /// with a sentinel and records the hit.</summary>
    private async Task<WebApplication> StartStubLauncherAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var stub = builder.Build();
        stub.Urls.Add("http://127.0.0.1:0");
        foreach (var verb in new[] { "restart", "start", "stop" })
        {
            var captured = verb;
            stub.MapPost($"/director/{captured}", () =>
            {
                lock (_aliceLauncherHits) _aliceLauncherHits.Add($"director/{captured}");
                return Results.Json(new { stub = StubSentinel, verb = captured });
            });
        }
        stub.MapPost("/launch", () =>
        {
            lock (_aliceLauncherHits) _aliceLauncherHits.Add("launch");
            return Results.Json(new { stub = StubSentinel, verb = "launch" });
        });
        await stub.StartAsync();
        _aliceLauncherPort = new Uri(stub.Urls.First()).Port;
        return stub;
    }

    internal const string StubSentinel = "alice-launcher-actually-reached";

    /// <summary>Put Alice's launcher into HER partition, at the stub's loopback port. An empty network address
    /// makes the relay dial 127.0.0.1:&lt;port&gt;.</summary>
    private void RegisterAliceLauncher() =>
        _gateway.Launchers.Upsert(_aliceTenant, new LauncherRegistrationRequest
        {
            MachineName = AliceMachine,
            Port = _aliceLauncherPort,
            NetworkAddress = "",
            Token = AliceLauncherToken,
            Pid = AliceLauncherPid,
            Version = "1.2.3",
        });

    private string[] AliceLauncherHits()
    {
        lock (_aliceLauncherHits) return _aliceLauncherHits.ToArray();
    }

    // =====================================================================================================
    // THE CAPABILITY: a hosted tenant controls its OWN machines.
    // =====================================================================================================

    [Fact]
    public async Task A_hosted_tenant_registers_lists_heartbeats_and_unregisters_its_own_machine()
    {
        // REGISTER through the route, then read the effect back out of the LISTING - a different route - so
        // neither can pass by being a no-op.
        var register = await Send("POST", "launchers/register", _aliceKey, new
        {
            machineName = AliceMachine,
            port = 7788,
            networkAddress = "alice-pc.example.ts.net",
            token = "tok",
            pid = AliceLauncherPid,
            version = "1.2.3",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var list = await GetLaunchers(_aliceKey);
        var entry = Assert.Single(list);
        Assert.Equal(AliceMachine, entry.MachineName);
        Assert.Equal(7788, entry.Port);
        Assert.Equal("alice-pc.example.ts.net", entry.NetworkAddress);

        // HEARTBEAT - 200 for her own machine, and the handler's OWN 410 for one she does not have.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("POST", $"launchers/{AliceMachine}/heartbeat", _aliceKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone,
            (await Send("POST", "launchers/NO-SUCH-MACHINE/heartbeat", _aliceKey)).StatusCode);

        // UNREGISTER - re-read the registry OBJECT directly, so the effect is observed at the write side and
        // not only through the route that reports it.
        Assert.Equal(HttpStatusCode.OK, (await Send("DELETE", $"launchers/{AliceMachine}", _aliceKey)).StatusCode);
        Assert.Null(_gateway.Launchers.Get(_aliceTenant, AliceMachine));
        Assert.Empty(await GetLaunchers(_aliceKey));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task A_hosted_tenant_drives_the_launcher_lifecycle_on_its_own_machine(string verb)
    {
        // THIS IS THE CAPABILITY THE DENY REMOVED, PROVED ON THE HOSTED GATEWAY. Not "not refused" - actually
        // reaching the launcher process and returning what that process said.
        RegisterAliceLauncher();

        var resp = await Send("POST", $"machines/{AliceMachine}/director/{verb}", _aliceKey);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(AliceMachine, doc.RootElement.GetProperty("machine").GetString());
        Assert.Equal(verb, doc.RootElement.GetProperty("verb").GetString());
        Assert.Equal(200, doc.RootElement.GetProperty("relayStatus").GetInt32());
        Assert.Contains(StubSentinel, doc.RootElement.GetProperty("payload").GetString()!, StringComparison.Ordinal);

        // The sentinel came back OUT OF the launcher, so the relay genuinely dialed it.
        Assert.Equal(new[] { $"director/{verb}" }, AliceLauncherHits());
    }

    [Fact]
    public async Task A_hosted_tenant_drives_a_generic_launch_on_its_own_machine()
    {
        RegisterAliceLauncher();

        var resp = await Send("POST", $"machines/{AliceMachine}/launch", _aliceKey,
            new { path = @"C:\Windows\System32\cmd.exe", args = "/c echo hi" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("launch", doc.RootElement.GetProperty("verb").GetString());
        Assert.Contains(StubSentinel, doc.RootElement.GetProperty("payload").GetString()!, StringComparison.Ordinal);
        Assert.Equal(new[] { "launch" }, AliceLauncherHits());
    }

    [Fact]
    public async Task The_session_spawn_route_is_served_to_a_hosted_tenant_and_is_no_longer_refused()
    {
        // The route --machine uses. It is reached, it runs its OWN validation, and it answers in its own terms.
        // A 400 here is the handler talking; under the old deny this was a 404 refusal that never reached it.
        var bad = await Send("POST", $"machines/{AliceMachine}/sessions", _aliceKey, new { agent = "ClaudeCode" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("repoPath is required", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // =====================================================================================================
    // THE HARD CONSTRAINT: same tenant only. Bob names Alice's machine and reaches NOTHING.
    // =====================================================================================================

    [Fact]
    public async Task One_tenant_cannot_enumerate_another_tenants_machines()
    {
        // The listing is what makes every other cross-tenant attack AIMABLE: it used to return every machine
        // name, network address, port and process id fleet-wide. Bob's listing must not carry any of Alice's.
        RegisterAliceLauncher();

        var bobsList = await GetLaunchers(_bobKey);
        Assert.Empty(bobsList);

        // Asserted on the RAW body too, so a future DTO field that leaked a machine name would redden here
        // rather than pass a typed assertion that simply does not look at it.
        var raw = await (await Send("GET", "launchers", _bobKey)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(AliceMachine, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AliceLauncherToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceLauncherPid.ToString(), raw, StringComparison.Ordinal);

        // The control: Alice's OWN listing does carry it. Without this, an unconditionally empty listing - a
        // broken route - would satisfy the assertions above while proving nothing about isolation.
        Assert.Equal(AliceMachine, Assert.Single(await GetLaunchers(_aliceKey)).MachineName);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task One_tenant_cannot_drive_the_launcher_lifecycle_on_another_tenants_machine(string verb)
    {
        RegisterAliceLauncher();

        var resp = await Send("POST", $"machines/{AliceMachine}/director/{verb}", _bobKey);

        // 404 "no launcher registered", the SAME answer a machine nobody registered gets. A distinguishable
        // "exists, but not yours" would let Bob enumerate Alice's machines one name at a time.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("no launcher registered", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // AND THE ACT ITSELF DID NOT HAPPEN: Alice's launcher process was never dialed. This is the assertion
        // that distinguishes a real partition from a Gateway that is merely broken.
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_execute_code_on_another_tenants_machine()
    {
        // The sharpest form of the leak: POST /machines/{machine}/launch forwards a caller-supplied path,
        // arguments and working directory to the launcher, which runs them.
        RegisterAliceLauncher();

        var resp = await Send("POST", $"machines/{AliceMachine}/launch", _bobKey,
            new { path = @"C:\Windows\System32\cmd.exe", args = "/c whoami", cwd = @"C:\" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_repoint_another_tenants_launcher_by_registering_over_it()
    {
        // OUTBOUND-REQUEST FORGERY, closed. POST /launchers/register overwrites a machine's stored token, port
        // and network address - so on a tenant-blind registry Bob could re-point Alice's relay at a host he
        // controls and harvest whatever the Gateway sent it. Bob registering ALICE-PC now writes into BOB's
        // partition and cannot touch hers.
        RegisterAliceLauncher();

        var resp = await Send("POST", "launchers/register", _bobKey, new
        {
            machineName = AliceMachine,
            port = 9999,
            networkAddress = "attacker.example.com",
            token = "attacker-token",
            pid = 1,
            version = "6.6.6",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Alice's row is untouched, read straight off the registry: her port, her address, her token.
        var alice = _gateway.Launchers.Get(_aliceTenant, AliceMachine);
        Assert.NotNull(alice);
        Assert.Equal(_aliceLauncherPort, alice!.Port);
        Assert.Equal("", alice.NetworkAddress);
        Assert.Equal(AliceLauncherToken, _gateway.Launchers.GetToken(_aliceTenant, AliceMachine));

        // Bob's write landed in BOB's partition - the two rows share a machine name and are different rows.
        var bob = _gateway.Launchers.Get(_bobTenant, AliceMachine);
        Assert.NotNull(bob);
        Assert.Equal(9999, bob!.Port);
        Assert.Equal("attacker.example.com", bob.NetworkAddress);

        // And Alice's relay still reaches ALICE's launcher, not the attacker's address.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("POST", $"machines/{AliceMachine}/director/start", _aliceKey)).StatusCode);
        Assert.Equal(new[] { "director/start" }, AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_delete_or_heartbeat_another_tenants_machine()
    {
        RegisterAliceLauncher();

        // A heartbeat for a machine Bob does not have is unknown to HIM - 410, the handler's own answer - and
        // does not refresh or reveal Alice's row.
        Assert.Equal(HttpStatusCode.Gone,
            (await Send("POST", $"launchers/{AliceMachine}/heartbeat", _bobKey)).StatusCode);

        // The unregister route reports success because Bob removed what Bob has, which is nothing. The
        // assertion that matters is on the REGISTRY: Alice's row survives.
        await Send("DELETE", $"launchers/{AliceMachine}", _bobKey);
        Assert.NotNull(_gateway.Launchers.Get(_aliceTenant, AliceMachine));
        Assert.Equal(AliceMachine, Assert.Single(await GetLaunchers(_aliceKey)).MachineName);
    }

    [Fact]
    public async Task One_tenant_cannot_spawn_a_session_on_another_tenants_machine_and_never_falls_back_locally()
    {
        // The route --machine uses, from the wrong tenant. Bob's partition has no launcher for ALICE-PC, so the
        // resolve fails in HIS partition and the spawn fails LOUD with 502.
        RegisterAliceLauncher();

        var resp = await Send("POST", $"machines/{AliceMachine}/sessions", _bobKey,
            new { repoPath = @"C:\repo", agent = "ClaudeCode" });

        // 502, never a 201. THE NO-LOCAL-FALLBACK RULE: an unreachable remote target must fail loudly and must
        // never quietly become a session somewhere else.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sessionId", body, StringComparison.OrdinalIgnoreCase);

        // And Alice's launcher was never asked to start anything on her behalf.
        Assert.Empty(AliceLauncherHits());
    }

    // =====================================================================================================
    // DENY BY DEFAULT, AND THE AUTH GATE IN FRONT OF EVERYTHING.
    // =====================================================================================================

    /// <summary>The whole family, one row per path AND verb - a property proved on GET while a sibling POST
    /// still writes is not a property.</summary>
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "POST",   "launchers/register" },
        { "POST",   $"launchers/{AliceMachine}/heartbeat" },
        { "DELETE", $"launchers/{AliceMachine}" },
        { "GET",    "launchers" },
        { "POST",   $"machines/{AliceMachine}/director/restart" },
        { "POST",   $"machines/{AliceMachine}/director/start" },
        { "POST",   $"machines/{AliceMachine}/director/stop" },
        { "POST",   $"machines/{AliceMachine}/launch" },
        { "POST",   $"machines/{AliceMachine}/sessions" },
    };

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task An_enrolled_key_bound_to_no_tenant_reaches_nothing(string verb, string path)
    {
        // DENY BY DEFAULT, ON EVERY ROUTE INCLUDING THE SPAWN. A key that resolves to no tenant must never
        // fall back to the Local or System partition, because that fallback is exactly what would hand an
        // unbound caller somebody's real fleet.
        //
        // IT IS REFUSED ONE GATE EARLIER THAN THE ROUTE, AND THAT IS WORTH STATING PRECISELY. On hosted,
        // DeviceRegistry.ResolveCredential treats a credential whose row carries no valid tenant binding as
        // REVOKED, so the host-wide auth middleware answers 401 and the handler never runs. The in-route 403
        // (ReqTenant -> NoTenant) is therefore a SECOND, INDEPENDENT gate rather than the one doing the work
        // here - and it stays, because a route in this family must not depend on the auth layer's current
        // policy to be safe. Asserting the 401 is asserting what actually happens; asserting a 403 would have
        // been asserting a path production never takes.
        RegisterAliceLauncher();

        var resp = await Send(verb, path, _unboundKey, BodyFor(path));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());

        // The control: the SAME request shape with a properly bound key is NOT 401, so the refusal above is
        // about this key's missing tenant and not about the request being malformed.
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await Send(verb, path, _aliceKey, BodyFor(path))).StatusCode);
    }

    [Fact]
    public void The_in_route_tenant_gate_is_a_second_independent_gate()
    {
        // The route-level deny-by-default, stated at the layer it lives in. HostedTenantBoundary is what every
        // route in this family resolves through, and on hosted it answers NULL for a key it cannot bind to a
        // tenant - never TenantId.Local and never TenantId.System. A route reading that null refuses.
        //
        // This is unit-level on purpose: the auth gate above means no real hosted request can carry an unbound
        // key this far, so the only honest way to prove the second gate is to ask it directly. If the auth
        // policy ever loosened, THIS is the gate that would still be holding.
        var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new AsyncLocalTenantContext(), _gateway.Devices);
        Assert.True(boundary.IsHosted);

        Assert.Null(boundary.ResolveForDeviceKey(_unboundKey));
        Assert.Null(boundary.ResolveForDeviceKey("not-a-key-at-all"));
        Assert.Null(boundary.ResolveForDeviceKey(null));

        // And the control: a properly bound key resolves to its OWN tenant, so the null above is a decision
        // and not a boundary that answers null to everything.
        Assert.Equal(_aliceTenant, boundary.ResolveForDeviceKey(_aliceKey));
        Assert.Equal(_bobTenant, boundary.ResolveForDeviceKey(_bobKey));
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task An_unauthenticated_caller_is_rejected_before_any_route(string verb, string path)
    {
        // The control that the tenant checks above are the SECOND gate and not the only one: without a
        // credential the host-wide auth middleware still refuses first.
        RegisterAliceLauncher();

        var req = new HttpRequestMessage(new HttpMethod(verb), path);
        if (BodyFor(path) is { } body) req.Content = JsonBody(body);
        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>A REAL, WELL-FORMED body for each write, so a refusal can never be a validation accident: the
    /// sessions route needs repoPath and register needs machineName/port/token, and either would answer 400
    /// rather than the status under test.</summary>
    private static object? BodyFor(string path) =>
        path.EndsWith("/sessions", StringComparison.Ordinal)
            ? new { repoPath = @"C:\repo", agent = "ClaudeCode" }
            : path.EndsWith("launchers/register", StringComparison.Ordinal)
                ? new { machineName = AliceMachine, port = 7788, token = "tok", pid = 11, version = "1.0.0" }
                : path.EndsWith("/launch", StringComparison.Ordinal)
                    ? new { path = @"C:\Windows\System32\cmd.exe", args = "/c whoami", cwd = @"C:\" }
                    : null;

    internal static HttpContent JsonBody(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Send(string verb, string path, string deviceKey, object? body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(verb), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (body is not null) req.Content = JsonBody(body);
        return _http.SendAsync(req);
    }

    private async Task<List<LauncherDto>> GetLaunchers(string deviceKey)
    {
        var resp = await Send("GET", "launchers", deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LauncherDto>>())!;
    }
}

/// <summary>
/// Boots ONLY the launcher/machine routes on an ephemeral port and hands the caller back the registry and the
/// spawner's call counters, so the write side can be re-read directly rather than through the very routes
/// under test.
///
/// <c>sendLauncherCommand</c> is deliberately null so the launcher STREAM arm returns null and dispatch falls
/// through to the REST RELAY. That is on purpose: the REST relay is the FALLBACK arm, and a fallback is the
/// path taken when something has already gone wrong, so it is the arm most worth exercising.
/// </summary>
internal sealed class MachineGroupProbeHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Http { get; init; }
    public required LauncherRegistry Launchers { get; init; }
    public required StubResolver Resolver { get; init; }
    public required WebApplication? StubLauncher { get; init; }
    public required int StubLauncherPort { get; init; }
    public required List<string> StubLauncherHits { get; init; }

    public const string SpawnedSessionId = "sid-self-host-sentinel";
    public const string StubLauncherSentinel = "stub-launcher-actually-reached";

    /// <summary>A resolver that returns a fixed target and COUNTS its calls, so "the spawn never happened" is statable.</summary>
    internal sealed class StubResolver : IDirectorTargetResolver
    {
        public int ResolveCount { get; private set; }

        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
        {
            ResolveCount++;
            return Task.FromResult(new DirectorTargetResult("d-probe", null));
        }
    }

    public static async Task<MachineGroupProbeHost> StartAsync(bool withStubLauncher = false)
    {
        // A stub launcher standing in for the real cc-launcher REST API on the target machine. When the relay
        // reaches it, it answers with a sentinel - so a served relay is proved by an effect that could only
        // come from the handler dialing out.
        WebApplication? stub = null;
        var hits = new List<string>();
        var stubPort = 0;
        if (withStubLauncher)
        {
            var stubBuilder = WebApplication.CreateBuilder();
            stubBuilder.Logging.ClearProviders();
            stub = stubBuilder.Build();
            stub.Urls.Add("http://127.0.0.1:0");
            foreach (var verb in new[] { "restart", "start", "stop" })
            {
                var captured = verb;
                stub.MapPost($"/director/{captured}", () =>
                {
                    lock (hits) hits.Add($"director/{captured}");
                    return Results.Json(new { stub = StubLauncherSentinel, verb = captured });
                });
            }
            stub.MapPost("/launch", () =>
            {
                lock (hits) hits.Add("launch");
                return Results.Json(new { stub = StubLauncherSentinel, verb = "launch" });
            });
            await stub.StartAsync();
            stubPort = new Uri(stub.Urls.First()).Port;
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var launchers = new LauncherRegistry();
        var resolver = new StubResolver();
        var spawner = new MachineSessionSpawner(resolver,
            (directorId, req, ct) => Task.FromResult<(bool, SessionDto?, string?)>(
                (true, new SessionDto { SessionId = SpawnedSessionId }, null)));

        MachineEndpoints.Map(app, launchers, spawner, sendLauncherCommand: null);

        await app.StartAsync();
        return new MachineGroupProbeHost
        {
            App = app,
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
            Launchers = launchers,
            Resolver = resolver,
            StubLauncher = stub,
            StubLauncherPort = stubPort,
            StubLauncherHits = hits,
        };
    }

    /// <summary>Register the stub launcher under <paramref name="machine"/> so the REST relay dials it on loopback.</summary>
    public void SeedStubLauncher(string machine) =>
        Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = machine,
            Port = StubLauncherPort,
            NetworkAddress = "",          // empty -> the relay dials 127.0.0.1:<port>
            Token = "stub-token",
            Pid = 1234,
            Version = "9.9.9",
        });

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
        if (StubLauncher is not null) await StubLauncher.DisposeAsync();
    }
}

/// <summary>
/// THE SELF-HOST CONTROL, STATED EXPLICITLY.
///
/// Self-host is the control for this entire hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. <see cref="LauncherRegistryEndpointTests"/> does not prove it: those tests never mention
/// <c>CC_GATEWAY_HOSTED</c> and pass only because the runner happens to leave it unset. If that ambient
/// default ever flipped - one leaked environment variable, one continuous-integration image change, one test
/// that forgot to restore it - they would keep passing while self-host was completely broken, because they
/// assert nothing about which mode they are in.
///
/// So this class sets the variable itself, to BOTH non-hosted values that occur in practice - absent, and
/// present-but-not-"1" - and asserts <see cref="GatewayHostedMode.IsHosted"/> is actually false before driving
/// anything. Each route is then proved SERVED by a POSITIVE EFFECT: a seeded registration read back out of the
/// listing, a heartbeat and an unregister whose effects are re-read from the registry, a relay that actually
/// reaches a STUB LAUNCHER and returns its sentinel payload, and a spawn that returns the stubbed session id.
///
/// WHAT IT IS THE CONTROL FOR NOW. It used to prove that the hosted DENY had not broken self-host as a side
/// effect. The deny is gone, and the thing to control for changed with it: on self-host there is exactly one
/// tenant (Local), so the composite (tenant, machine) key degenerates to the machine name it always was. These
/// tests prove that degeneration is exact - the same routes, the same relay, the same behaviour - so the
/// tenant work cannot have quietly cost the single-operator deployment anything.
/// </summary>
public sealed class SelfHostMachineControlTests : IDisposable
{
    private const string Machine = "PROBE-MACHINE";
    private readonly string? _priorHosted;

    public SelfHostMachineControlTests() =>
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");

    public void Dispose() => Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);

    /// <summary>
    /// Puts the process into a STATED non-hosted mode and proves the statement took, so no test below can
    /// silently be running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    /// <summary>null = the variable is absent. "0" = present and explicitly not hosted. Both are real non-hosted deployments.</summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task Registration_listing_heartbeat_and_unregister_all_still_work_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        await using var probe = await MachineGroupProbeHost.StartAsync();

        var register = await probe.Http.PostAsJsonAsync("/launchers/register", new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Port = 7788,
            NetworkAddress = "probe-machine.example.ts.net",
            Token = "tok",
            Pid = 4242,
            Version = "1.2.3",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var list = await probe.Http.GetFromJsonAsync<List<LauncherDto>>("/launchers");
        var entry = Assert.Single(list!);
        Assert.Equal(Machine, entry.MachineName);
        Assert.Equal(7788, entry.Port);
        Assert.Equal("probe-machine.example.ts.net", entry.NetworkAddress);
        Assert.Equal(4242, entry.Pid);
        Assert.Equal("1.2.3", entry.Version);

        var beat = await probe.Http.PostAsync($"/launchers/{Machine}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.Contains("\"ok\":true", await beat.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Gone,
            (await probe.Http.PostAsync("/launchers/NO-SUCH-MACHINE/heartbeat", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await probe.Http.DeleteAsync($"/launchers/{Machine}")).StatusCode);
        Assert.Null(probe.Launchers.Get(TenantId.Local, Machine));
        Assert.Empty((await probe.Http.GetFromJsonAsync<List<LauncherDto>>("/launchers"))!);
    }

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_director_lifecycle_relay_still_reaches_the_launcher_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        await using var probe = await MachineGroupProbeHost.StartAsync(withStubLauncher: true);
        probe.SeedStubLauncher(Machine);

        foreach (var verb in new[] { "restart", "start", "stop" })
        {
            var resp = await probe.Http.PostAsync($"/machines/{Machine}/director/{verb}",
                HostedTenantMachineControlTests.JsonBody(new { exePath = @"C:\builds\cc-director7.exe" }));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(Machine, doc.RootElement.GetProperty("machine").GetString());
            Assert.Equal(verb, doc.RootElement.GetProperty("verb").GetString());
            Assert.Equal(200, doc.RootElement.GetProperty("relayStatus").GetInt32());
            Assert.Contains(MachineGroupProbeHost.StubLauncherSentinel,
                doc.RootElement.GetProperty("payload").GetString()!, StringComparison.Ordinal);
        }

        Assert.Equal(new[] { "director/restart", "director/start", "director/stop" }, probe.StubLauncherHits);
    }

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_generic_launch_relay_still_reaches_the_launcher_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        await using var probe = await MachineGroupProbeHost.StartAsync(withStubLauncher: true);
        probe.SeedStubLauncher(Machine);

        var resp = await probe.Http.PostAsync($"/machines/{Machine}/launch",
            HostedTenantMachineControlTests.JsonBody(new { path = @"C:\Windows\System32\cmd.exe", args = "/c echo hi" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("launch", doc.RootElement.GetProperty("verb").GetString());
        Assert.Contains(MachineGroupProbeHost.StubLauncherSentinel,
            doc.RootElement.GetProperty("payload").GetString()!, StringComparison.Ordinal);
        Assert.Equal(new[] { "launch" }, probe.StubLauncherHits);
    }

    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task Starting_a_session_on_another_machine_still_works_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        await using var probe = await MachineGroupProbeHost.StartAsync();

        var resp = await probe.Http.PostAsync($"/machines/{Machine}/sessions",
            HostedTenantMachineControlTests.JsonBody(new { repoPath = @"C:\repo", agent = "ClaudeCode" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(MachineGroupProbeHost.SpawnedSessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, probe.Resolver.ResolveCount);

        var bad = await probe.Http.PostAsync($"/machines/{Machine}/sessions",
            HostedTenantMachineControlTests.JsonBody(new { agent = "ClaudeCode" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("repoPath is required", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

/// <summary>
/// THE PURGE THE OLD DENY DEMANDED, DISCHARGED BY PROOF RATHER THAN BY ASSERTION.
///
/// The deny's un-deny instruction said: "do NOT simply remove this deny... THEN purge the launcher and
/// launcher-connection registries - the /launcher-stream hub keeps writing machine-name-keyed rows behind this
/// deny", and it was explicitly deny-closed - "assume the purge is required until write-coverage is proven
/// complete". Two things had to be true before that instruction could be retired, and this class establishes
/// the second one; the first (every writer is tenant-keyed) is established by
/// <see cref="HostedTenantMachineControlTests"/> and by <c>LauncherHubTests</c>.
///
/// THE PROOF: both registries hold their state ONLY in memory, for the lifetime of the process. There is no
/// launcher entity in the database, nothing serialises either registry to disk, and nothing reloads either at
/// startup - so a fresh registry starts EMPTY, and a Gateway restart is a complete purge of everything that
/// ever accumulated behind the deny. Deploying this change restarts the Gateway, which performs that purge.
/// No purge routine is written, because a routine that clears an already-empty store is a no-op that reads
/// like a safeguard - and would leave the next maintainer believing there was persisted state to protect.
///
/// This is asserted rather than asserted-in-a-comment because the property is a LIVE one: if someone later
/// gives the launcher registry a persisted backing store, the purge question reopens, and these tests are what
/// notices.
/// </summary>
public sealed class LauncherRegistryIsProcessLifetimeOnlyTests
{
    [Fact]
    public void A_fresh_launcher_registry_holds_nothing()
    {
        // If the registry ever gained a persisted backing store, a fresh instance would rehydrate it and this
        // would redden - which is exactly the moment the purge question would need reopening.
        var registry = new LauncherRegistry();

        Assert.Empty(registry.ListLaunchers(TenantId.Local));
        Assert.Null(registry.Get(TenantId.Local, "ANY-MACHINE"));
        Assert.Null(registry.GetToken(TenantId.Local, "ANY-MACHINE"));
    }

    [Fact]
    public void A_second_launcher_registry_cannot_see_the_first_ones_rows()
    {
        // State does not outlive the object, so it cannot outlive the process. A registry that shared a store
        // with another instance would fail here, and a shared store is the only way rows could survive a
        // restart.
        var first = new LauncherRegistry();
        first.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = "MACHINE-A", Port = 7788, Token = "tok", Pid = 1, Version = "1.0.0",
        });
        Assert.NotNull(first.Get(TenantId.Local, "MACHINE-A"));

        var second = new LauncherRegistry();
        Assert.Null(second.Get(TenantId.Local, "MACHINE-A"));
        Assert.Empty(second.ListLaunchers(TenantId.Local));
    }

    [Fact]
    public void A_fresh_launcher_connection_registry_holds_nothing()
    {
        var registry = new Streaming.LauncherConnectionRegistry();

        Assert.Null(registry.GetActiveConnectionId(TenantId.Local, "ANY-MACHINE"));
        Assert.False(registry.IsStreamConnected(TenantId.Local, "ANY-MACHINE"));
    }

    [Fact]
    public void A_second_launcher_connection_registry_cannot_see_the_first_ones_rows()
    {
        var first = new Streaming.LauncherConnectionRegistry();
        first.RegisterConnection(TenantId.Local, "MACHINE-A", "conn-1");
        Assert.True(first.IsStreamConnected(TenantId.Local, "MACHINE-A"));

        var second = new Streaming.LauncherConnectionRegistry();
        Assert.False(second.IsStreamConnected(TenantId.Local, "MACHINE-A"));
    }
}
