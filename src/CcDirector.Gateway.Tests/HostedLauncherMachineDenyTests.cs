using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
/// Issue #1917: the launcher and machine-control family is DENIED on the hosted Gateway.
///
/// THE DEFECT. The family is TENANT-BLIND BY CONSTRUCTION - there is no tenant dimension anywhere in the
/// path. <see cref="LauncherRegistry"/> keys on machine NAME alone, <c>LauncherConnectionRegistry</c> keys on
/// machine NAME alone, and <c>LauncherHub.Hello</c> binds a connection to a machine name with NO tenant
/// resolution at all - in direct contrast to <c>DirectorHub.Hello</c>, which aborts when the device key
/// resolves to no tenant. So on hosted, ANY authenticated device key could enumerate every tenant's machines
/// through GET /launchers - which returns every machine name, network address, port and process id
/// fleet-wide, so the identifier the write routes need is not even guesswork - and then drive the machine
/// routes AGAINST ANOTHER TENANT'S MACHINE. That is cross-machine CODE EXECUTION
/// (POST /machines/{machine}/launch forwards a caller-supplied path, arguments and working directory) plus
/// OUTBOUND-REQUEST FORGERY (POST /launchers/register overwrites a machine's stored token, port and network
/// address, re-pointing the relay at an arbitrary host).
///
/// WHY THE USUAL PROTECTION DOES NOT APPLY. Elsewhere on the hosted Gateway, bare-identifier Director routes
/// are inert cross-tenant because the command rides <c>SendCommandAsync</c>, which refuses to resolve a
/// tunnel connection with no tenant in scope. THAT PROTECTION LIVES IN THE TRANSPORT, NOT IN THE ROUTE. This
/// family has three dispatch arms and only the Director-tunnel arm is gated; the launcher stream arm resolves
/// purely on machine name, and the launcher REST relay - the FALLBACK taken when the stream arm returns null -
/// dials the launcher's stored address with its stored bearer token. The FAILURE path is the ungated one.
///
/// A DENY, NOT A PARTITION. On shared hosted infrastructure A TENANT DOES NOT OWN A MACHINE. There is no
/// correct per-tenant answer to serve here - only a leak to close. A partition would require inventing an
/// ownership relation that was never recorded, which is a half-partition: worse than an honest refusal
/// because it looks like isolation. And it is a REFUSAL, never an empty result: an empty GET /launchers would
/// be a FALSE statement (a fleet with no machines) where a refusal is merely absent - the /healthz mistake.
///
/// THE REFUSAL IS IDENTIFIABLE AS ITSELF. Every denied route serves 404 with
/// <c>application/json</c> and a body whose property set is EXACTLY <c>{ error }</c> carrying
/// <see cref="MachineEndpoints.HostedRefusal"/> verbatim. A bare 404 would be indistinguishable from a route
/// that does not exist, and an allow-list on the property set reddens automatically on any extra leaked
/// field, unlike a substring check.
///
/// ONE GROUP FILTER, NOT A GUARD PER ROUTE. The refusal is an endpoint filter on the route group, so it runs
/// before every route in the group INCLUDING ROUTES THAT DO NOT EXIST YET. A guard repeated in each handler
/// passes exactly the same tests as a group filter for the routes that exist today, which is precisely what
/// makes it dangerous. <see cref="HostedLauncherMachineGroupFilterTests"/> proves the difference with a
/// brand-new probe route, in BOTH directions (refused on hosted, served on self-host).
///
/// THE WRITE IS STOPPED ON THE HTTP SURFACE, BUT NOT EVERYWHERE - AND THAT DECIDES THE UN-DENY. This file
/// proves the denied write routes leave no state behind: after a refused POST /launchers/register the
/// registry is re-read directly and is still empty, and after a refused POST /machines/{machine}/sessions the
/// spawner was never invoked. But the /launcher-stream SignalR hub is NOT in this route group:
/// <c>LauncherHub.Hello</c> still writes a machine-name-keyed connection row into
/// <c>LauncherConnectionRegistry</c> behind this deny. So tenant-blind state can still ACCUMULATE, and the
/// un-deny is the later tenant-key unit PLUS A PURGE of the launcher and launcher-connection registries.
/// Deny-closed on the safe side: assume the purge is required until write-coverage is proven complete.
///
/// SELF-HOST IS PROVED, NOT INHERITED. <see cref="HostedLauncherMachineSelfHostControlTests"/> sets
/// <c>CC_GATEWAY_HOSTED</c> ITSELF to BOTH non-hosted forms (absent, and present-but-"0"), asserts
/// <see cref="GatewayHostedMode.IsHosted"/> is actually false, and then proves each route is genuinely SERVED
/// by a positive effect - a seeded registration read back, a relay reaching a STUB LAUNCHER and returning its
/// sentinel payload, a spawned session id - never merely by the refusal being absent.
///
/// REVERT-PROOF RECIPE. In <c>src/CcDirector.Gateway/Api/MachineEndpoints.cs</c> DELETE the
/// <c>app.AddEndpointFilter(...)</c> block outright, leaving <c>var app = outer.MapGroup("");</c> in place so
/// the group still exists and the file compiles - the hosted deny is then absent ENTIRELY with no per-route
/// guard put back in its place. Deleting is the only correct revert: wrapping it in <c>if (false)</c> leaves
/// unreachable code, which is a BUILD ERROR here, and a test run after a failed build silently executes the
/// previous binary and reports a false pass. Rebuild, CONFIRM ZERO ERRORS, verify by diff that the mutation
/// is actually present in the source, then run the FULL suite - never a filter over these classes, which
/// could not see whether some existing test already covered the behaviour nor any collateral damage.
///
/// THE REDS ARE PRE-REGISTERED BELOW, WRITTEN AND PUSHED BEFORE THE ARM WAS EVER RUN. That ordering is the
/// whole point: A PREDICTED RED THAT DOES NOT APPEAR IS A FINDING. A list filled in afterwards from what was
/// observed cannot produce that finding at all - whatever reddens is by definition what was expected, so a
/// mutation that silently failed to apply, or that reddened the wrong tests, reads as a clean result. It
/// guards the other direction too: MORE reds than predicted means the filter was carrying something that was
/// not accounted for, and that is to be understood rather than accepted.
///
/// PREDICTION: EXACTLY 15 TEST CASES REDDEN, ALL OF THEM IN THIS FILE, AND NOTHING ELSE IN THE SUITE MOVES.
///
///   HostedLauncherMachineDenyTests.Every_launcher_and_machine_route_is_refused_to_an_enrolled_tenant
///       - ALL NINE theory rows.
///   HostedLauncherMachineDenyTests.The_launcher_listing_leaks_no_machine_on_hosted
///   HostedLauncherMachineDenyTests.The_refusal_is_not_an_empty_launcher_list
///   HostedLauncherMachineGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own
///   HostedLauncherMachineGroupFilterTests.A_refused_registration_writes_nothing_into_the_registry
///   HostedLauncherMachineGroupFilterTests.A_refused_spawn_never_reaches_the_resolver
///   HostedLauncherMachineGroupFilterTests.A_refused_launch_never_dials_the_launcher
///
/// WHERE EACH RED IS PREDICTED TO ARRIVE, because a red that arrives from the fixture is VOID and only a red
/// that arrives from an assertion about what was served proves anything:
///
///   Five of the nine theory rows redden on the STATUS assertion, because with the filter gone the handler's
///   own answer is a different status: register -> 201 Created, heartbeat on an unregistered machine -> 410
///   Gone, unregister -> 200 OK, the listing -> 200 OK, and the spawn -> 502 Bad Gateway (no Director on that
///   machine; the spawner fails loud rather than falling back).
///
///   The other FOUR - the three director lifecycle verbs and the generic launch - are predicted to redden on
///   the PROPERTY-SET assertion and NOT on the status assertion. With no launcher registered, the relay's own
///   refusal is ALSO 404 with application/json, so status and media type still match; what separates them is
///   the body, which carries { error, machine } with a different error string instead of { error } alone.
///   THAT IS THE SHARPEST PREDICTION IN THIS FILE and it is the reason the refusal is asserted as an exact
///   property set rather than as a status code.
///
/// THOSE FOUR ROWS ARE A TRAP DETECTOR, NOT MERELY AN EXPECTATION - AND THE TRAP IS
/// ABSENCE-PROVED-TWICE-PRESENCE-NEVER. The hosted deny answers 404, and so does the relay's own "no launcher
/// registered on that machine". Two different absences, identical on the wire except in the body. So:
///
///   - If those four come back GREEN under the mutation, the deny was INDISTINGUISHABLE from
///     no-launcher-registered all along, and every hosted pass on them proved nothing.
///   - IF THOSE FOUR REDDEN ON THE STATUS ASSERTION RATHER THAN ON THE PROPERTY-SET ASSERTION, THAT IS A
///     FINDING ABOUT THE DENY'S DISTINGUISHABILITY - NOT A HARMLESS VARIATION IN THE ARM. It means something
///     other than the body carried the difference, and the property-set assertion - the only thing separating
///     a deny from a missing route on this family - was never the load-bearing check it is claimed to be.
///
/// Write that down, because the failure mode is a READER failure: four reds appear, a reviewer counts four
/// against a prediction of four, and nobody notices they ARRIVED BY THE WRONG ROUTE. That is the
/// arrival-classification rule turned on this file's own prediction. A red is classified by WHERE IT ARRIVES,
/// and that applies to the predicted reds exactly as it applies to the unexpected ones.
///
/// DECLARED UNKNOWN, and note WHICH DIMENSION is uncertain - uncertain about the ROUTE, certain about the
/// OUTCOME. Those four rows depend on the launcher STREAM arm returning null inside a full GatewayHost booted
/// with streamMode true, so the REST relay arm is the one that answers; whether that holds is what cannot be
/// predicted from reading alone. What is NOT unknown is that they MUST REDDEN. A blanket "not sure what
/// happens here" would have been unfalsifiable; naming the one uncertain dimension leaves every other claim
/// testable, and the status-versus-property-set arrival is then a FINDING rather than a shrug.
///
/// MUST STAY GREEN - the controls, and a control that moves with the change under test is not a control:
///   HostedLauncherMachineDenyTests.An_unauthenticated_caller_is_still_rejected
///   HostedLauncherMachineGroupFilterTests.A_route_outside_the_group_still_serves_on_hosted
///   ALL TEN cases of HostedLauncherMachineSelfHostControlTests (five methods, two non-hosted forms each)
///
/// AND ZERO REDS ANYWHERE ELSE IN THE SUITE. The twelve existing launcher and machine test files run under
/// the runner's ambient non-hosted default, where this filter is a pass-through, so deleting it cannot reach
/// them. If one of them reddens, the ambient default is not what it is believed to be - which would be a
/// finding about every other hosted test in this assembly, not a detail about this one.
///
/// THE RECONCILIATION PROTOCOL. Three rules, in this order, and the ORDER is load-bearing.
///
///   1. RECONCILE AGAINST THE BASELINE TOTAL, NOT AGAINST RESTORED PASSED. The tempting identity - passed
///      plus failed under the mutation equals passed under the restore - holds ONLY IF SKIPPED NEVER MOVES.
///      Skips in this suite are environment-gated and CAN move, so that identity balances by accident: when a
///      skip count shifts it either breaks for a reason that will be chased for nothing, or it silently
///      ABSORBS a real discrepancy. The check with nothing left out is
///      PASSED + FAILED + SKIPPED == THE BASELINE TOTAL, on every arm including baseline and restore.
///
///   2. RECONCILE BEFORE SCORING, NOT AFTER. Reconciliation is a PRECONDITION for the arm counting at all,
///      not a post-hoc validation of a score already formed. Score first and a truncated run has already been
///      read as a result by the time the total is checked. A short total means the arm is
///      UNPROVEN-TRUNCATED: it is not scored in EITHER direction, and it does NOT become "a mutation that
///      reddened fewer tests than predicted".
///
///   3. THE TRUNCATION TRAP BEARS DIRECTLY ON THE FIFTEEN ABOVE. If a run truncates, FEWER THAN FIFTEEN reds
///      appear. Under the rule this file already holds - a predicted red that does not appear is a FINDING -
///      a truncated arm would present itself as a finding ABOUT THE DENY. IT IS NOT. It is a finding about
///      the HARNESS. So the total is checked FIRST and the fifteen are compared only afterwards. A
///      pre-registration makes truncation MORE dangerous, not less, because it hands truncation a ready-made
///      story to be mistaken for.
///
/// BASELINE PRECONDITIONS, both checked before the first run:
///   - THE WHOLE SOURCE TREE IS CLEAN, not merely the Gateway project. A lost TEST edit falsifies an arm as
///     thoroughly as a lost production edit, and a tree-scoped-to-one-project check would not see it.
///   - THE WORKING HEAD EQUALS THE PUSHED HEAD. Cleanliness alone is not sufficient: a stash leaves the tree
///     CLEAN at the pinned head, so a tree that looks perfect can be missing the very repair under test, and
///     a guard checking only cleanliness would ADMIT it.
///
/// THE RUN PLAN IS EXACTLY THREE FULL-SUITE RUNS, and the path actually taken is the path registered:
/// baseline, arm, restore. There is no slice-then-full escalation and no separate canary gate here - all
/// three arms are the FULL suite, so no ordering can slip a fourth run in behind the registered three. On a
/// contended serial box a fourth run spends someone else's slot.
/// </summary>
public sealed class HostedLauncherMachineDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Machine = "MACHINE-VICTIM";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string? _priorHosted;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-launcher-deny-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself rather
        // than inheriting whatever the runner happened to leave set, and proves the statement took.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the STRONGEST caller hosted has. The point is that even
        // this one is refused: no credential makes "this tenant owns that machine" true on shared hardware.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every route in the family, driven end to end through a REAL hosted Gateway by a fully enrolled
    /// tenant's device key. The theory data is the whole denied surface, one row per path AND verb - a deny
    /// proved on GET while a sibling POST still writes is not a deny.
    /// </summary>
    public static TheoryData<string, string> DeniedRoutes => new()
    {
        { "POST",   "launchers/register" },
        { "POST",   $"launchers/{Machine}/heartbeat" },
        { "DELETE", $"launchers/{Machine}" },
        { "GET",    "launchers" },
        { "POST",   $"machines/{Machine}/director/restart" },
        { "POST",   $"machines/{Machine}/director/start" },
        { "POST",   $"machines/{Machine}/director/stop" },
        { "POST",   $"machines/{Machine}/launch" },
        { "POST",   $"machines/{Machine}/sessions" },
    };

    [Theory]
    [MemberData(nameof(DeniedRoutes))]
    public async Task Every_launcher_and_machine_route_is_refused_to_an_enrolled_tenant(string verb, string path)
    {
        var resp = await Send(verb, path, _key);

        // STATUS AND MEDIA TYPE BEFORE ANY PARSE. Parsing is itself an assertion about format: with the guard
        // deleted these routes serve other shapes entirely, and a JsonDocument.Parse crash would prove only
        // that the mutation broke something upstream - it cannot say WHAT was served in place of the refusal.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_launcher_listing_leaks_no_machine_on_hosted()
    {
        // The listing is what makes the write routes aimable: it returns every machine name, network address,
        // port and process id fleet-wide. Seeding a machine directly into the registry and then proving the
        // hosted response cannot possibly carry it states the disclosure in its own terms - an empty list
        // would satisfy "no leak" while being a false statement about the fleet, so the refusal is asserted too.
        _gateway.Launchers.Upsert(new LauncherRegistrationRequest
        {
            MachineName = "SOMEONE-ELSES-PC",
            Port = 7999,
            NetworkAddress = "someone-elses-pc.example.ts.net",
            Token = "victim-token",
            Pid = 4242,
            Version = "1.2.3",
        });

        var resp = await Send("GET", "launchers", _key);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SOMEONE-ELSES-PC", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("someone-elses-pc.example.ts.net", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7999", body, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", body, StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refusal_is_not_an_empty_launcher_list()
    {
        // The /healthz lesson applied on purpose: an empty list is a FALSE statement (a fleet with no
        // machines) where an absent one is merely absent. A caller must never be handed a shaped, empty
        // answer that reads as isolation. The exact-property-set assertion above is what enforces this; this
        // states the intent in the terms the mistake is usually made in.
        var body = await (await Send("GET", "launchers", _key)).Content.ReadAsStringAsync();
        Assert.NotEqual("[]", body.Trim());
        Assert.Contains("not available", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the family up as a side effect of running before the gate.
        // Without a key the host-wide auth middleware still refuses FIRST, so the 404s above are the filter
        // and not the absence of a gate.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("launchers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.PostAsync($"machines/{Machine}/launch", JsonBody(new { path = "cmd.exe" }))).StatusCode);
    }

    /// <summary>
    /// Asserts the body is the hosted refusal and NOTHING ELSE, by parsing the JSON and comparing the WHOLE
    /// property set to a one-name allow-list. A substring check cannot see an extra leaked field; enumerating
    /// the property set reddens automatically on anything extra without this file being touched.
    /// </summary>
    internal static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(MachineEndpoints.HostedRefusal, doc.RootElement.GetProperty("error").GetString());
    }

    internal static HttpContent JsonBody(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Send(string verb, string path, string deviceKey)
    {
        var req = new HttpRequestMessage(new HttpMethod(verb), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (verb == "POST")
        {
            // A REAL, WELL-FORMED body for each write, so a refusal can never be a validation accident. The
            // sessions route needs repoPath or its own handler 400s; register needs machineName/port/token or
            // its own handler 400s. Both would be a 400, not the 404 asserted - but sending a valid body means
            // the assertion is about the deny and nothing else.
            req.Content = path.EndsWith("/sessions", StringComparison.Ordinal)
                ? JsonBody(new { repoPath = @"C:\repo", agent = "ClaudeCode" })
                : path.EndsWith("launchers/register", StringComparison.Ordinal)
                    ? JsonBody(new { machineName = Machine, port = 7788, token = "tok", pid = 11, version = "1.0.0" })
                    : JsonBody(new { path = @"C:\Windows\System32\cmd.exe", args = "/c whoami", cwd = @"C:\" });
        }
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

/// <summary>
/// Boots ONLY the launcher/machine group on an ephemeral port and hands the caller back the route group, the
/// registry and the spawner's call counters. That is what makes the future-route proof possible at all: the
/// group is created INSIDE <see cref="MachineEndpoints.Map"/>, so nothing outside that method could otherwise
/// state a property about routes added to it. Owning the registry object also lets a test re-read the write
/// side directly rather than through the very routes under test.
///
/// <c>sendLauncherCommand</c> is deliberately null so the launcher STREAM arm returns null and dispatch falls
/// through to the REST RELAY - the ungated fallback arm the defect actually rides. The self-host control
/// therefore exercises the same arm an attacker would.
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

    /// <summary>A resolver that returns a fixed target and COUNTS its calls, so "the write never happened" is statable.</summary>
    internal sealed class StubResolver : IDirectorTargetResolver
    {
        public int ResolveCount { get; private set; }

        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
        {
            ResolveCount++;
            return Task.FromResult(new DirectorTargetResult("d-probe", null));
        }
    }

    public static async Task<MachineGroupProbeHost> StartAsync(
        Action<RouteGroupBuilder>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null,
        bool withStubLauncher = false)
    {
        // A stub launcher standing in for the real cc-launcher REST API on the target machine. When the relay
        // reaches it, it answers with a sentinel - so a served relay is proved by an effect that could only
        // come from the handler dialing out, not by the refusal merely being absent.
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

        var group = MachineEndpoints.Map(app, launchers, spawner, sendLauncherCommand: null);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

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
        Launchers.Upsert(new LauncherRegistrationRequest
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
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal is a filter on the ROUTE GROUP, so it covers routes that
/// have not been written yet.
///
/// A guard repeated in every handler passes exactly the same tests as a group filter for the routes that
/// exist today, which is precisely why it is dangerous - the difference only shows up on the route somebody
/// adds NEXT, when it is open by default and nothing fails. On THIS family, "open by default" means
/// cross-machine code execution. That difference is not observable by driving the nine routes that exist, so
/// this class maps a BRAND-NEW probe route onto the group and asserts it is refused with no deny of its own
/// written anywhere. Its mirror - the same probe path SERVED with hosted mode explicitly off - lives in
/// <see cref="HostedLauncherMachineSelfHostControlTests"/>: one direction alone cannot tell a working gate
/// apart from a brick.
/// </summary>
public sealed class HostedLauncherMachineGroupFilterTests : IDisposable
{
    internal const string ProbePayloadSentinel = "probe-payload-that-must-never-be-served-on-hosted";
    internal const string ProbePath = "/machines/added-after-the-deny-was-written";

    private readonly string? _priorHosted;

    public HostedLauncherMachineGroupFilterTests()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in
    /// <see cref="MachineEndpoints"/> mentions this path and no guard is written for it here - the only thing
    /// standing between the caller and the probe payload is the group filter. Replace the filter with
    /// per-handler guards and this test serves the probe payload with a 200, which is the future-route hole
    /// stated out loud.
    /// </summary>
    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own()
    {
        await using var probe = await MachineGroupProbeHost.StartAsync(
            mapIntoGroup: group => group.MapGet(ProbePath, () => Results.Json(new { probe = ProbePayloadSentinel })));

        var resp = await probe.Http.GetAsync(ProbePath);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain(ProbePayloadSentinel, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await HostedLauncherMachineDenyTests.AssertBodyIsNothingButTheRefusal(resp);
    }

    /// <summary>
    /// CONTROL: the filter is scoped to this group, not a blanket refusal on the whole application. A route
    /// mapped OUTSIDE the group still serves on hosted, so the passing tests here are the filter doing its
    /// job rather than the host refusing everything.
    /// </summary>
    [Fact]
    public async Task A_route_outside_the_group_still_serves_on_hosted()
    {
        await using var probe = await MachineGroupProbeHost.StartAsync(
            mapOutsideGroup: routes => routes.MapGet("/not-a-machine-route", () => Results.Json(new { ok = true })));

        var resp = await probe.Http.GetAsync("/not-a-machine-route");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("true", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE WRITE IS STOPPED, NOT JUST THE READ - on the HTTP surface. A deny that only silences the response
    /// while the handler still mutates state is a DEFERRED LEAK: it looks closed and un-denies onto a
    /// poisoned registry. The registry is re-read DIRECTLY here, not through the routes under test, so this
    /// cannot be satisfied by the read being denied too.
    ///
    /// This does NOT establish full write-coverage for the family: <c>LauncherHub.Hello</c> writes a
    /// machine-name-keyed connection row over the /launcher-stream SignalR hub, which is not in this route
    /// group and is therefore untouched by this deny. The un-deny is consequently the tenant-key unit PLUS A
    /// PURGE.
    /// </summary>
    [Fact]
    public async Task A_refused_registration_writes_nothing_into_the_registry()
    {
        await using var probe = await MachineGroupProbeHost.StartAsync();

        var resp = await probe.Http.PostAsync("/launchers/register", HostedLauncherMachineDenyTests.JsonBody(
            new { machineName = "ATTACKER-REDIRECT", port = 31337, networkAddress = "evil.example.com", token = "t", version = "1.0.0" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await HostedLauncherMachineDenyTests.AssertBodyIsNothingButTheRefusal(resp);

        // Read the write side directly. Nothing landed - so the outbound-request-forgery primitive
        // (re-pointing a machine's relay at an arbitrary host) never armed.
        Assert.Null(probe.Launchers.Get("ATTACKER-REDIRECT"));
        Assert.Empty(probe.Launchers.ListLaunchers());
    }

    /// <summary>
    /// The same statement for the spawn route, whose FAILURE branch is the one that starts a Director on
    /// another machine through the relay. The resolver counts its calls, so "the handler never ran" is an
    /// observation rather than an inference from the status code.
    /// </summary>
    [Fact]
    public async Task A_refused_spawn_never_reaches_the_resolver()
    {
        await using var probe = await MachineGroupProbeHost.StartAsync();

        var resp = await probe.Http.PostAsync("/machines/SOMEONE-ELSES-PC/sessions",
            HostedLauncherMachineDenyTests.JsonBody(new { repoPath = @"C:\repo", agent = "ClaudeCode" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await HostedLauncherMachineDenyTests.AssertBodyIsNothingButTheRefusal(resp);
        Assert.Equal(0, probe.Resolver.ResolveCount);
    }

    /// <summary>
    /// The relay is not merely refused - it never DIALS. The stub launcher records every hit it receives, so
    /// a deny that answered 404 to the caller while still forwarding the launch would redden here.
    /// </summary>
    [Fact]
    public async Task A_refused_launch_never_dials_the_launcher()
    {
        await using var probe = await MachineGroupProbeHost.StartAsync(withStubLauncher: true);
        probe.SeedStubLauncher("SOMEONE-ELSES-PC");

        var resp = await probe.Http.PostAsync("/machines/SOMEONE-ELSES-PC/launch",
            HostedLauncherMachineDenyTests.JsonBody(new { path = @"C:\Windows\System32\cmd.exe", args = "/c whoami" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await HostedLauncherMachineDenyTests.AssertBodyIsNothingButTheRefusal(resp);
        Assert.Empty(probe.StubLauncherHits);
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
/// present-but-not-"1" - and asserts <see cref="GatewayHostedMode.IsHosted"/> is actually false before
/// driving anything. Each route is then proved SERVED by a POSITIVE EFFECT: a seeded registration read back
/// out of the listing, a heartbeat and an unregister whose effects are re-read from the registry, a relay
/// that actually reaches a STUB LAUNCHER and returns its sentinel payload, and a spawn that returns the
/// stubbed session id. Absence of the refusal is never the assertion - an empty-but-successful response would
/// satisfy that while being a dead surface.
///
/// These tests must stay GREEN through the revert described on <see cref="HostedLauncherMachineDenyTests"/>.
/// A control that moves with the change under test is not a control.
/// </summary>
public sealed class HostedLauncherMachineSelfHostControlTests : IDisposable
{
    private const string Machine = "PROBE-MACHINE";
    private readonly string? _priorHosted;

    public HostedLauncherMachineSelfHostControlTests() =>
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

        // REGISTER - and read the effect back out of the LISTING, which is a different route, so neither
        // route can pass by being a no-op.
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

        // HEARTBEAT - 200 for a known machine, and 410 for an unknown one. The 410 is the handler's OWN
        // negative answer, which is only reachable if the route is genuinely served.
        var beat = await probe.Http.PostAsync($"/launchers/{Machine}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.Contains("\"ok\":true", await beat.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Gone,
            (await probe.Http.PostAsync("/launchers/NO-SUCH-MACHINE/heartbeat", content: null)).StatusCode);

        // UNREGISTER - and re-read the registry OBJECT directly, so the effect is observed at the write side
        // rather than only through the route that reports it.
        Assert.Equal(HttpStatusCode.OK, (await probe.Http.DeleteAsync($"/launchers/{Machine}")).StatusCode);
        Assert.Null(probe.Launchers.Get(Machine));
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
                HostedLauncherMachineDenyTests.JsonBody(new { exePath = @"C:\builds\cc-director7.exe" }));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(Machine, doc.RootElement.GetProperty("machine").GetString());
            Assert.Equal(verb, doc.RootElement.GetProperty("verb").GetString());
            Assert.Equal(200, doc.RootElement.GetProperty("relayStatus").GetInt32());
            // The sentinel came back OUT OF THE STUB LAUNCHER, so the relay genuinely dialled - the route is
            // served, not merely un-refused.
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
            HostedLauncherMachineDenyTests.JsonBody(new { path = @"C:\Windows\System32\cmd.exe", args = "/c echo hi" }));

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
            HostedLauncherMachineDenyTests.JsonBody(new { repoPath = @"C:\repo", agent = "ClaudeCode" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(MachineGroupProbeHost.SpawnedSessionId, doc.RootElement.GetProperty("sessionId").GetString());
        // The spawn actually ran the resolve-then-create path; the deny is not silently short-circuiting it.
        Assert.Equal(1, probe.Resolver.ResolveCount);

        // The handler's OWN validation is still reachable too - a 400 here is the route serving, and it is
        // distinguishable from the hosted 404, which is the whole reason the deny is not a bare 404.
        var bad = await probe.Http.PostAsync($"/machines/{Machine}/sessions",
            HostedLauncherMachineDenyTests.JsonBody(new { agent = "ClaudeCode" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("repoPath is required", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SECOND HALF OF THE FUTURE-ROUTE PROOF: the same probe path that
    /// <see cref="HostedLauncherMachineGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own"/>
    /// finds refused on hosted must be SERVED with hosted mode explicitly off, in both non-hosted forms.
    /// Without this half, "the filter refuses everything, always" would pass every hosted assertion in this
    /// file while having silently killed the family for self-host too.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        await using var probe = await MachineGroupProbeHost.StartAsync(
            mapIntoGroup: group => group.MapGet(HostedLauncherMachineGroupFilterTests.ProbePath,
                () => Results.Json(new { probe = "served" })));

        var resp = await probe.Http.GetAsync(HostedLauncherMachineGroupFilterTests.ProbePath);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("served", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
