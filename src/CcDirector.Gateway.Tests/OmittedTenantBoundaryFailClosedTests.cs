using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening (release 2026-07-31, finding CR-7), the FAIL-OPEN case at the four remaining
/// resolver sites and the boundary class itself: a HOSTED Gateway whose endpoints were wired with NO tenant
/// boundary must REFUSE, never quietly serve the shared Local (self-host) partition.
///
/// This extends the pattern of <see cref="DictationOmittedTenantBoundaryTests"/> - the instance of this
/// defect that was found and closed first - to every site the release review listed:
///
///   1. <c>GatewayEndpoints.ResolveReadTenant</c> - the shared resolver behind dozens of routes
///      (sessions, directors, recordings, settings, wingman voice, machine relays). Probed at
///      <c>GET /directors</c>, and the machine surface (<c>MachineEndpoints.ReqTenant</c>, which
///      delegates here) is probed separately at <c>GET /launchers</c>.
///   2. <c>ActivityEventEndpoints.ResolveTenant</c> - probed at <c>GET /activity-events</c>.
///   3. <c>HistoryEndpoints.ResolveTenant</c> - probed at <c>GET /history/sessions</c>.
///   4. <c>PromptEndpoints.ResolveTenant</c> - probed at <c>GET /prompts</c>.
///   5. <c>HostedTenantBoundary</c> itself - a hosted process wiring a boundary that could only answer
///      Local now THROWS at construction, and at resolution when hosted mode appeared after construction.
///
/// Each resolver used to answer <see cref="TenantId.Local"/> whenever its boundary argument was absent,
/// WITHOUT asking <see cref="GatewayHostedMode.IsHosted"/> - so one forgotten optional argument silently
/// collapsed every hosted tenant into the shared Local partition, and nothing failed loud to say so. TWO
/// DEFENCES ship together, exactly as on the dictation endpoint. The compile-time one: the boundary
/// parameter is REQUIRED at every Map signature, so the tests below force the miswire with <c>null!</c> -
/// the accidental version no longer builds. The runtime one, proved here: the resolvers gate on
/// <see cref="GatewayHostedMode.IsHosted"/> itself and answer null (a REFUSAL) for a missing or
/// non-hosted-wired boundary on a hosted process.
///
/// THE MECHANICS, taken from the dictation class:
///  - Every request is replayed against an IDENTICAL harness differing ONLY in the boundary argument. The
///    wired twin answering 200 proves the route exists and the request is well-formed, so the 403 on the
///    unwired twin is the guard refusing - never a missing route answered 404/405 before endpoint selection,
///    and never a rejected request shape.
///  - Both twins run the same stand-in for the auth layer: the request arrives AUTHENTICATED, its device
///    identity bound to a real minted account tenant. Authentication is not the variable; the boundary is.
///  - The refusal is asserted POSITIVELY: the concrete 403, the exact deny message, and the response's
///    property set as an ALLOW-LIST (a substring check cannot see an extra leaked field).
///  - Where the Local partition can cheaply hold a secret (a Director row, a launcher row, a prompt), it is
///    seeded FIRST and the refusal body is checked to not carry it - because serving exactly that secret is
///    what the unwired host did before the fix, and what it does again under the revert proof.
///
/// REVERT-PROVE: restore any resolver to <c>boundary is null ? TenantId.Local : ...</c> and its test here
/// goes RED - the unwired host answers 200 and (where seeded) hands back the Local partition's data. Restore
/// the <see cref="HostedTenantBoundary"/> guards to silent-Local and both boundary tests go red.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED and CC_DIRECTOR_ROOT
/// here is safe; both are restored in DisposeAsync.
/// </summary>
public sealed class OmittedTenantBoundaryFailClosedTests : IAsyncLifetime
{
    /// <summary>The one message every deny path produces. Asserted exactly, so a 403 from anywhere else fails.</summary>
    private const string DenyMessage = "no tenant is bound to this request";

    private const string LocalSecretMachine = "local-partition-secret-machine";
    private const string LocalSecretPrompt = "local-partition-secret-prompt-text";

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-omitted-boundary-" + Guid.NewGuid().ToString("N"));

    private string? _priorHosted;
    private string? _priorRoot;
    private TenantId _tenant;

    public Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        Directory.CreateDirectory(_storageRoot);

        // A canonical lowercase minted GUID - the one tenant spelling the registry produces and the
        // boundary's identity resolution accepts as a real account tenant.
        _tenant = new TenantId(Guid.NewGuid().ToString("D"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
        return Task.CompletedTask;
    }

    // ===== site 1: GatewayEndpoints.ResolveReadTenant, probed at GET /directors =====================

    [Fact]
    public async Task DirectorsList_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // THE CONCRETE DISCLOSURE: a Director registered in the LOCAL partition - where every self-host
        // Director lives. Before the fix the missing boundary resolved to Local and GET /directors handed
        // this row (machine name, operating system user, process id, liveness) to any authenticated hosted
        // caller. The wired twin gets the same Local row PLUS one in the caller's own partition, so the
        // partition choice - not an empty registry - is what each response proves.
        var unwiredRegistry = new DirectorRegistry(Path.Combine(_storageRoot, "directors-unwired"));
        unwiredRegistry.RegisterFromStream("local-director", LocalSecretMachine, "local-user", "1.0", 11,
            DateTime.UtcNow, TenantId.Local);

        await using (var unwired = await Host.StartAsync(_tenant,
            app => GatewayEndpoints.Map(app, unwiredRegistry, "test", "test-token", tenantBoundary: null!)))
        {
            var (status, body) = await Get(unwired.Http, "/directors");
            AssertRefusal(status, body);
            Assert.DoesNotContain(LocalSecretMachine, body, StringComparison.Ordinal);
        }

        // Positive companion: identical host, the ONE argument supplied. The caller's own partition is
        // served - its own Director comes back, the Local partition's does not - so the 403 above is the
        // guard refusing, not a route that does not exist or a registry with nothing to leak.
        var wiredRegistry = new DirectorRegistry(Path.Combine(_storageRoot, "directors-wired"));
        wiredRegistry.RegisterFromStream("local-director", LocalSecretMachine, "local-user", "1.0", 11,
            DateTime.UtcNow, TenantId.Local);
        wiredRegistry.RegisterFromStream("own-director", "own-machine", "own-user", "1.0", 12,
            DateTime.UtcNow, _tenant);

        await using var wired = await Host.StartAsync(_tenant,
            app => GatewayEndpoints.Map(app, wiredRegistry, "test", "test-token", tenantBoundary: WiredBoundary()));
        var (wiredStatus, wiredBody) = await Get(wired.Http, "/directors");
        Assert.Equal(HttpStatusCode.OK, wiredStatus);
        Assert.Contains("own-director", wiredBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalSecretMachine, wiredBody, StringComparison.Ordinal);
    }

    // ===== site 2: MachineEndpoints.ReqTenant, probed at GET /launchers =============================

    [Fact]
    public async Task LaunchersList_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // A launcher row in the Local partition: machine name, port, process id - the machine-control
        // surface's inventory. Registered directly on the registry (the store the route reads), so the
        // refusal below is about the ROUTE's partition choice, not about an empty store.
        var unwiredLaunchers = new LauncherRegistry();
        unwiredLaunchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = LocalSecretMachine,
            Port = 4321,
            Token = "local-token",
            Pid = 99,
            Version = "1.0",
        });

        await using (var unwired = await Host.StartAsync(_tenant,
            app => MachineEndpoints.Map(app, unwiredLaunchers, Spawner(), boundary: null!)))
        {
            var (status, body) = await Get(unwired.Http, "/launchers");
            AssertRefusal(status, body);
            Assert.DoesNotContain(LocalSecretMachine, body, StringComparison.Ordinal);
        }

        // Positive companion: the wired twin's list really runs, inside the caller's own (empty) partition.
        var wiredLaunchers = new LauncherRegistry();
        wiredLaunchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = LocalSecretMachine,
            Port = 4321,
            Token = "local-token",
            Pid = 99,
            Version = "1.0",
        });

        await using var wired = await Host.StartAsync(_tenant,
            app => MachineEndpoints.Map(app, wiredLaunchers, Spawner(), boundary: WiredBoundary()));
        var (wiredStatus, wiredBody) = await Get(wired.Http, "/launchers");
        Assert.Equal(HttpStatusCode.OK, wiredStatus);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(wiredBody).RootElement.ValueKind);
        Assert.DoesNotContain(LocalSecretMachine, wiredBody, StringComparison.Ordinal);
    }

    // ===== site 3: ActivityEventEndpoints.ResolveTenant, probed at GET /activity-events ============

    [Fact]
    public async Task ActivityEvents_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        using var unwiredDb = new GatewayDbTestHarness();
        await using (var unwired = await Host.StartAsync(_tenant,
            app => ActivityEventEndpoints.Map(app, new ActivityEventStore(unwiredDb.Open()), tenantBoundary: null!)))
        {
            var (status, body) = await Get(unwired.Http, "/activity-events");
            AssertRefusal(status, body);
        }

        // Positive companion: the wired twin serves the caller's own (empty) partition through the SAME
        // ambient context the boundary enters, which is exactly the production wiring.
        using var wiredDb = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var store = new ActivityEventStore(wiredDb.Open(ambient));
        await using var wired = await Host.StartAsync(_tenant,
            app => ActivityEventEndpoints.Map(app, store, new HostedTenantBoundary(ambient, new DeviceRegistry())));
        var (wiredStatus, wiredBody) = await Get(wired.Http, "/activity-events");
        Assert.Equal(HttpStatusCode.OK, wiredStatus);
        Assert.Equal(0, JsonDocument.Parse(wiredBody).RootElement.GetProperty("count").GetInt32());
    }

    // ===== site 4: HistoryEndpoints.ResolveTenant, probed at GET /history/sessions ==================

    [Fact]
    public async Task HistorySessions_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        using var unwiredDb = new GatewayDbTestHarness();
        await using (var unwired = await Host.StartAsync(_tenant,
            app => HistoryEndpoints.Map(app, new SessionHistoryStore(unwiredDb.Open()), tenantBoundary: null!)))
        {
            var (status, body) = await Get(unwired.Http, "/history/sessions");
            AssertRefusal(status, body);
        }

        using var wiredDb = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var store = new SessionHistoryStore(wiredDb.Open(ambient));
        await using var wired = await Host.StartAsync(_tenant,
            app => HistoryEndpoints.Map(app, store, new HostedTenantBoundary(ambient, new DeviceRegistry())));
        var (wiredStatus, wiredBody) = await Get(wired.Http, "/history/sessions");
        Assert.Equal(HttpStatusCode.OK, wiredStatus);
        Assert.Equal(0, JsonDocument.Parse(wiredBody).RootElement.GetProperty("count").GetInt32());
    }

    // ===== site 5: PromptEndpoints.ResolveTenant, probed at GET /prompts ============================

    [Fact]
    public async Task Prompts_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // The seeded disclosure: a prompt with today's timestamp in the LOCAL partition - the file-backed
        // log takes the tenant explicitly, so the Local partition is populated without any ambient scope.
        // GET /prompts defaults to today's range, so before the fix the unwired host answered 200 carrying
        // this record's full text.
        var log = new GatewayPromptLog(Path.Combine(_storageRoot, "prompts"));
        log.Append(TenantId.Local, new[]
        {
            new PromptRecord
            {
                TsUtc = DateTime.UtcNow,
                SessionId = "local-session",
                Role = "user",
                TimestampFromAgent = true,
                CharCount = LocalSecretPrompt.Length,
                WordCount = 1,
                Text = LocalSecretPrompt,
            },
        });
        Assert.Contains(log.Read(TenantId.Local, DateTime.UtcNow.Date, DateTime.UtcNow.Date),
            r => r.Text == LocalSecretPrompt); // the disclosure claim below is meaningless if the seed failed

        await using (var unwired = await Host.StartAsync(_tenant,
            app => PromptEndpoints.Map(app, log, tenantBoundary: null!)))
        {
            var (status, body) = await Get(unwired.Http, "/prompts");
            AssertRefusal(status, body);
            Assert.DoesNotContain(LocalSecretPrompt, body, StringComparison.Ordinal);
        }

        // Positive companion: the wired twin reads the SAME log through its own partition - served, and
        // still not the Local partition's text.
        await using var wired = await Host.StartAsync(_tenant,
            app => PromptEndpoints.Map(app, log, tenantBoundary: WiredBoundary()));
        var (wiredStatus, wiredBody) = await Get(wired.Http, "/prompts");
        Assert.Equal(HttpStatusCode.OK, wiredStatus);
        Assert.Equal(0, JsonDocument.Parse(wiredBody).RootElement.GetProperty("count").GetInt32());
        Assert.DoesNotContain(LocalSecretPrompt, wiredBody, StringComparison.Ordinal);
    }

    // ===== site 6: HostedTenantBoundary itself ======================================================

    [Fact]
    public void Boundary_construction_throws_on_a_hosted_process_without_the_ambient_context()
    {
        // A hosted process wiring the boundary over the single-tenant context could only ever answer Local -
        // every account collapsed into one partition. That is a wiring defect and it fails LOUD now. The
        // positive companion is every wired harness in this class: the same construction over the
        // AsyncLocalTenantContext, under the same hosted mode, succeeds.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry()));
        Assert.Contains("hosted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boundary_resolution_throws_when_hosted_mode_appeared_after_construction()
    {
        // The one path the constructor guard cannot see: built while the process was NOT hosted (legal -
        // that is every self-host boundary), resolved after hosted mode turned on. Resolving Local there
        // is the same silent collapse, so resolution refuses too.
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry());

        // Control first, while still self-host: the same boundary resolves Local - so the throws below are
        // the hosted flip, not a boundary that cannot resolve at all.
        Assert.True(boundary.ResolveForDeviceKey("any-key")!.Value.IsLocal);

        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.Throws<InvalidOperationException>(() => boundary.ResolveForDeviceKey("any-key"));
        Assert.Throws<InvalidOperationException>(
            () => boundary.ResolveRequestTenant(new Microsoft.AspNetCore.Http.DefaultHttpContext()));
    }

    // ===== the refusal itself =======================================================================

    /// <summary>
    /// The refusal, asserted positively: the concrete 403, the exact message only the deny paths emit, and
    /// the response's property set as an ALLOW-LIST - a substring check for what must be absent cannot see
    /// an extra field that leaked, and the hazard here is a body carrying another partition's data.
    /// </summary>
    private static void AssertRefusal(HttpStatusCode status, string body)
    {
        Assert.Equal(HttpStatusCode.Forbidden, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(DenyMessage, root.GetProperty("error").GetString());
    }

    // ===== helpers ==================================================================================

    /// <summary>
    /// A hosted-wired boundary for routes that resolve the tenant from the stashed identity and do not
    /// enter a store scope shared with a database (directors, launchers, prompts - their stores take the
    /// tenant explicitly). Sites whose store reads the ambient context build the boundary over that same
    /// context instead, exactly as production does.
    /// </summary>
    private static HostedTenantBoundary WiredBoundary()
        => new(new AsyncLocalTenantContext(), new DeviceRegistry());

    /// <summary>A spawner whose resolver refuses to be used - GET /launchers never spawns anything.</summary>
    private static MachineSessionSpawner Spawner()
        => new(new UnusedResolver(), (directorId, req, ct) =>
            Task.FromResult<(bool, SessionDto?, string?)>((false, null, "not used by this test")));

    private sealed class UnusedResolver : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
            => throw new NotSupportedException("the launcher list route never resolves a spawn target");
    }

    private static async Task<(HttpStatusCode status, string body)> Get(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A minimal host mapping ONLY the site under test. Twin instances are built by the same code with the
    /// same middleware and differ in EXACTLY ONE argument - the boundary - so a difference in behaviour
    /// between them can only be that argument. The middleware is the auth layer's stand-in: every request
    /// arrives authenticated, its device identity bound to a real minted account tenant, which is what
    /// makes the unwired twin's 403 an authorization refusal rather than a missing login.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Http { get; }

        private Host(WebApplication app, HttpClient http)
        {
            _app = app;
            Http = http;
        }

        internal static async Task<Host> StartAsync(TenantId tenant, Action<WebApplication> map)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");

            app.Use(async (ctx, next) =>
            {
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
                    "test-device", tenant.Value, DeviceRegistry.DefaultDeviceType, DeviceRegistry.StatusActive);
                await next();
            });

            map(app);
            await app.StartAsync();
            return new Host(app, new HttpClient
            {
                BaseAddress = new Uri(app.Urls.First()),
                Timeout = TimeSpan.FromSeconds(30),
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
