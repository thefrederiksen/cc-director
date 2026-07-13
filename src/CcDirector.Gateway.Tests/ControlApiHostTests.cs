using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end smoke tests for the Director's internal Control API.
/// Uses a real SessionManager (with no sessions) so we exercise the
/// HTTP plumbing, JSON serialization, and routing.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private bool _shutdownRequested;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () =>
        {
            _shutdownRequested = true;
            return Task.CompletedTask;
        }, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();

        // Best-effort cleanup of the registration file
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var dto = await _client.GetFromJsonAsync<HealthDto>("healthz");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.Equal(0, dto.Sessions);
        Assert.Equal(1, dto.Directors);
        Assert.Equal("1.0.0-test", dto.Version);
    }

    // Gateway Cleanup mission (the cut): the Director loopback Control API no longer serves ANY session
    // routes - GET /sessions (list), GET /sessions/{sid}, POST /sessions/{sid}/prompt, GET .../buffer and
    // POST .../interrupt are all deleted. The roster is read from the Gateway push store now, and every
    // per-session verb is dispatched over the tunnel into the shared verb core (SessionCommandExecutor).
    // The following seven tests, which drove those deleted loopback routes, were removed because the routes
    // are gone and their behaviour is proven against the verb core elsewhere (byte-identical logic):
    //   * Sessions_empty_when_none_running        -> roster now = push store (TunnelRosterPushReadProofTests).
    //   * Sessions_get_by_id_returns_400_for_bad_format  -> SessionReadExecutorTests
    //         .DispatchAsync_Snapshot_InvalidSessionId_ReturnsBadRequest (the GET /sessions/{sid} bad-guid guard).
    //   * Sessions_get_by_id_returns_404_for_unknown_guid -> SessionReadExecutorTests
    //         .DispatchAsync_Snapshot_MissingSession_ReturnsNotFound.
    //   * Sessions_prompt_returns_400_for_empty_text  -> SessionCommandExecutorTests
    //         .DispatchAsync_Prompt_EmptyText_ReturnsBadRequest.
    //   * Sessions_prompt_returns_404_for_unknown_guid -> SessionCommandExecutorTests
    //         .DispatchAsync_Prompt_MissingSession_ReturnsNotFound.
    //   * Sessions_buffer_returns_404_for_unknown_guid -> SessionReadExecutorTests
    //         .DispatchAsync_Buffer_MissingSession_ReturnsNotFound.
    //   * Sessions_interrupt_returns_404_for_unknown_guid -> SessionCommandExecutorTests
    //         .DispatchAsync_Interrupt_MissingSession_ReturnsNotFound.
    // (The four "404 for unknown guid" tests still passed by accident - a deleted route returns 404 for
    // route-not-found, not session-not-found - so they green-lit machinery that no longer exists.)

    [Fact]
    public async Task Shutdown_triggers_callback()
    {
        Assert.False(_shutdownRequested);
        var resp = await _client.PostAsync("shutdown", null);
        Assert.True(resp.IsSuccessStatusCode);

        // Callback runs on a Task.Delay(100) so wait a beat
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!_shutdownRequested && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(_shutdownRequested);
    }

    [Fact]
    public void Registration_file_exists_after_start()
    {
        var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
        Assert.True(File.Exists(f), $"Registration file should exist at {f}");

        var json = File.ReadAllText(f);
        Assert.Contains(_host.DirectorId, json);
        Assert.Contains($"127.0.0.1:{_host.Port}", json);
    }

    [Fact]
    public void IsListening_isTrue_and_noStartupError_after_successful_start()
    {
        // The host started successfully in InitializeAsync, so the UI's Control-API indicator
        // stays hidden (StartupError null) and remote access is reported up.
        Assert.True(_host.IsListening);
        Assert.Null(_host.StartupError);
    }

    // Gateway Cleanup mission (the cut): POST /sessions (create) is deleted from the Director loopback -
    // session creation is a tunnel verb now. The two issue #800/#807 create tests that drove that route
    // (name-at-birth applied, and a weak explicit name rejected with 400) were removed because the exact
    // create core - including the name-at-birth validation - is proven against the verb core elsewhere:
    //   * name-at-birth applied  -> SessionCommandExecutorTests.DispatchAsync_Create_MakesSessionWithRightAgentNameAndWingman
    //         and .DispatchAsync_Create_ExplicitName_IsNotAutoNamed (the create verb returns the meaningful
    //         name on the SessionDto).
    //   * weak explicit name rejected (issue #800) -> SessionCommandExecutorTests
    //         .DispatchAsync_Create_WeakExplicitName_ReturnsBadRequest (bare repo folder name -> BadRequest).
    // The "subsequent GET shows the same name with no PATCH" half is gone with the deleted GET /sessions/{sid}
    // loopback route; the create verb returning the name is the surviving, byte-identical guarantee.
}

/// <summary>
/// Regression for the "session never turns red when the Control API can't bind" bug
/// (2026-06-15). The per-session state services (SessionStatusWingman + TerminalStateDetector)
/// used to start mid-StartAsync, AFTER PortAllocator.Allocate. When every port in [7879..7898]
/// was busy, Allocate threw and aborted StartAsync before those services ran, so the desktop
/// badge (Session.StatusColor) froze on its last colour and a silent session could never flip
/// to the red "needs you" state. StartSessionStateServices() now runs up front, independent of
/// the bind. These tests start ONLY those services (never call StartAsync, so no port is ever
/// bound) and prove the badge pipeline is live.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionStateServicesDecouplingTests
{
    [Fact]
    public async Task StateServices_DriveBadgeColour_WithoutAnyControlApiBind()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);

        // Start the state services directly. We never call StartAsync, so Kestrel never binds a
        // port -- exactly the state a Director is in after "all ports in [7879..7898] busy".
        host.StartSessionStateServices();
        try
        {
            // Pipe-mode session: no process is spawned, but the SessionStatusWingman wires its
            // activity handler so StatusColor tracks ActivityState.
            var session = sm.CreatePipeModeSession(Path.GetTempPath());

            // Simulate a session that has already taken its first turn. A brand-new session is
            // painted green ("ready") at a turn-end by design - it is parked at its prompt, not
            // needing you. This test exercises the genuine "needs you" RED path, which applies
            // only once IsBrandNew has cleared (it clears when the first prompt is submitted).
            session.IsBrandNew = false;

            // Drive the activity state the way TerminalStateDetector would (byte -> Working;
            // QuietThreshold of silence -> WaitingForInput). The wingman is the sole writer of
            // StatusColor; if it is running, the badge follows -- with no Control API bound.
            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal("blue", session.StatusColor);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal("red", session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }

    [Fact]
    public void ReportStartupFailure_SetsErrorAndRaisesEvent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Before any failure: healthy defaults so the UI indicator stays hidden.
            Assert.False(host.IsListening);
            Assert.Null(host.StartupError);

            var raised = 0;
            host.StartupStatusChanged += () => raised++;

            // Simulate the App boundary catching a bind failure (e.g. all ports busy).
            host.ReportStartupFailure("All ports in range 7879..7898 are busy.");

            Assert.False(host.IsListening);
            Assert.Equal("All ports in range 7879..7898 are busy.", host.StartupError);
            Assert.Equal(1, raised);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task StartSessionStateServices_IsIdempotent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Calling twice must not throw or double-wire (StartAsync also calls it once).
            host.StartSessionStateServices();
            host.StartSessionStateServices();

            var session = sm.CreatePipeModeSession(Path.GetTempPath());
            // Past its brand-new "ready" (green) window - see the note in the test above; this
            // asserts the genuine "needs you" red path. A pipe session starts already in
            // WaitingForInput, so drive Working first to guarantee a real transition back into
            // WaitingForInput fires the wingman handler (SetActivityState ignores same-state).
            session.IsBrandNew = false;
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal("red", session.StatusColor);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }
}

// Gateway Cleanup mission (the cut): the DirectorSessionIdentityFieldsTests class (issue #335) was removed.
// It asserted the Director's OWN /sessions and /sessions/{sid} loopback endpoints stamped the four identity
// fields (machineName, user, tailnetEndpoint, viewUrl). Those loopback routes are deleted, AND - verified in
// production - the Director no longer stamps identity at all: its push snapshot is built by
// ControlApiHost.SnapshotFullSessions -> ControlEndpoints.Map(session, directorId), the plain overload whose
// machineName/user/tailnetEndpoint default to "" (and viewUrl derives from an empty tailnet, so it is ""
// too). The resolveTailnetEndpoint resolver is now an unused leftover parameter on ControlEndpoints.Map(app,
// ...). By design the identity fields are stamped by the GATEWAY aggregation pass for pushed rows, exactly as
// ControlEndpoints.Map documents ("the Gateway aggregator stamps machine/user/tailnet/view-url during
// aggregation, for pushed and pulled alike"). The #335 regression is therefore covered at the Gateway
// aggregation seam, not the Director: SessionsAggregationTests
//   * Aggregator_back_compat_enriches_old_director_empty_identity_fields (a Director sending EMPTY identity -
//     which is now EVERY Director - gets all four fields enriched by the Gateway), and
//   * Aggregator_preserves_director_supplied_identity_fields_and_does_not_overwrite_them (mixed-version
//     back-compat).
// There is no reachable Director-side seam that stamps identity, so re-pointing these three tests to a
// Director snapshot seam would assert the OPPOSITE of the shipped behaviour (the fields are empty on the
// Director side by design). See the worker report: flagged for Manager review, not deleted silently.

/// <summary>
/// Issue #697: when the fixed Control-API range [7879..7898] is genuinely exhausted, the production
/// loopback host falls back to an ephemeral loopback port instead of disabling the Control API. It
/// stays listening (no startup error, so the desktop "Control API down / free a port" notice never
/// fires) and answers normally. Isolation: CC_DIRECTOR_ROOT points config/registration at a temp
/// root; the PortAllocationOverride seam forces "exhausted" WITHOUT touching real OS ports; and
/// SuppressServeProvisioning keeps the test from mutating the host machine's real Tailscale serve table.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostEphemeralFallbackTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public ControlApiHostEphemeralFallbackTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-697-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StartAsync_FixedRangeExhausted_FallsBackToEphemeralPortAndStaysListening()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(
            sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: false,                          // production loopback path (not the test ephemeral seam)
            directorId: Guid.NewGuid().ToString(),            // isolate the registration file
            instancesDirectory: Path.Combine(_root, "instances"))
        {
            PortAllocationOverride = _ => null,               // simulate a genuinely exhausted fixed range
            SuppressServeProvisioning = true,                 // do not mutate the real Tailscale serve table
        };

        try
        {
            var port = await host.StartAsync();

            // Bound a port OUTSIDE the fixed range, and Port reflects the OS-assigned value.
            Assert.True(port < PortAllocator.PortRangeStart || port > PortAllocator.PortRangeEnd,
                $"fallback must bind a port outside [{PortAllocator.PortRangeStart}..{PortAllocator.PortRangeEnd}], got {port}");
            Assert.Equal(port, host.Port);

            // A successful fallback is NOT a failure: no startup error -> the "Control API down" notice never fires.
            Assert.True(host.IsListening);
            Assert.Null(host.StartupError);

            // The Control API actually answers on the ephemeral port. Gateway Cleanup mission (the cut):
            // GET /sessions is gone from the loopback floor, so the liveness probe uses a floor route that
            // survives - GET /healthz - which is all this test needs to prove the fallback host is listening.
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
            var resp = await client.GetAsync("healthz");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }
}

/// <summary>
/// Issue #846: the session-number backfill is now wired into production. These tests prove the two
/// wirings that were missing (BackfillNumbers had no caller before): (1) a single backfill pass runs
/// at Director startup (ControlApiHost.StartAsync), numbering any tracked session that lacks a number;
/// and (2) the "backfill-numbers" verb triggers the same backfill on a RUNNING Director (no restart),
/// returns the count newly numbered, is idempotent (a second call returns 0), and the assigned numbers
/// are unique and within the 100-999 range. Gateway Cleanup mission (the cut): (2) used to be the
/// POST /admin/backfill-numbers loopback route; that route is deleted and the backfill is now a tunnel
/// verb dispatched through the shared command core, so the test drives that verb core directly.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionNumberBackfillTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;

    public SessionNumberBackfillTests()
    {
        // Isolate the machine-global director root (issue #1055) so StartAsync_NumbersTrackedSessionsThatLackANumber
        // writes its registration file into a fresh temp root instead of the fleet machine's real director root,
        // keeping the startup-backfill test deterministic regardless of the host machine's config.json.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-backfill-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StartAsync_NumbersTrackedSessionsThatLackANumber()
    {
        // Arrange: a session that is tracked but carries NO number (the pre-#820 / restored-without-a
        // -number state). CreatePipeModeSession numbers it at creation, so clear it to simulate the gap.
        var sm = new SessionManager(new AgentOptions());
        var session = sm.CreatePipeModeSession(Path.GetTempPath());
        session.Number = null;

        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Act: starting the Director runs the one-time startup backfill.
            await host.StartAsync();

            // Assert: the previously-unnumbered session now has a number in range.
            Assert.NotNull(session.Number);
            Assert.InRange(session.Number.Value, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{host.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* test cleanup */ }
        }
    }

    [Fact]
    public async Task BackfillVerb_NumbersUnnumberedSessions_AndIsIdempotent()
    {
        // Gateway Cleanup mission (the cut): POST /admin/backfill-numbers is deleted from the Director
        // loopback; the backfill runs on a RUNNING Director as the "backfill-numbers" tunnel verb, dispatched
        // through the SAME shared command core the Gateway reaches over the tunnel. Re-pointed from the old
        // loopback POST to that verb core, preserving every original assertion: two tracked-but-unnumbered
        // sessions get numbered (no restart), the count is reported, the numbers are unique and in range, and
        // a second call is idempotent (0). The numbers are read straight off the live session records now that
        // there is no loopback GET /sessions to enumerate.
        var sm = new SessionManager(new AgentOptions());
        try
        {
            // Arrange: two tracked sessions, both made unnumbered (the gap the backfill closes).
            var a = sm.CreatePipeModeSession(Path.GetTempPath());
            var b = sm.CreatePipeModeSession(Path.GetTempPath());
            a.Number = null;
            b.Number = null;

            // Act: trigger the backfill verb on the running Director (no restart).
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { CommandId = "bf1", Verb = "backfill-numbers", SessionId = "" });

            // Assert: both were numbered and the count is reported.
            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<BackfillNumbersResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(2, resp!.Assigned);
            Assert.NotNull(a.Number);
            Assert.NotNull(b.Number);

            // The live roster now reflects the numbers (proves the running-Director backfill, no restart).
            var numbers = sm.ListSessions().Select(s => s.Number).ToList();
            Assert.All(numbers, n => Assert.NotNull(n));

            // Uniqueness + range (AC5).
            var values = numbers.Select(n => n!.Value).ToList();
            Assert.Equal(values.Count, values.Distinct().Count());
            Assert.All(values, n =>
                Assert.InRange(n, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber));

            // Act + Assert: a SECOND call changes nothing (idempotent, AC4).
            var result2 = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { CommandId = "bf2", Verb = "backfill-numbers", SessionId = "" });
            Assert.Equal(DirectorCommandStatus.Ok, result2.Status);
            var resp2 = JsonSerializer.Deserialize<BackfillNumbersResponse>(result2.BodyJson ?? "", Json);
            Assert.NotNull(resp2);
            Assert.Equal(0, resp2!.Assigned);
        }
        finally { sm.Dispose(); }
    }

    // Gateway Cleanup mission (the cut): BackfillEndpoint_RequiresBearerToken_WhenAuthEnabled was removed. It
    // proved the loopback POST /admin/backfill-numbers route was bearer-protected (401 without a token). That
    // route is deleted - the backfill is a tunnel verb now, whose authentication is the authenticated SignalR
    // tunnel connection itself, not a per-call bearer header - so there is no longer a loopback route to
    // protect and nothing left to assert.
}
