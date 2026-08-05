using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director host's lifecycle, without a listener.
///
/// Remove-the-network-port mission, phase 5: the HTTP smoke tests that used to live here - healthz,
/// the prompt-delivery-failures read, and "shutdown is no longer a route" - are gone WITH THE WHOLE
/// LISTENER. There are no routes left to smoke-test: nothing binds, so "route X answers/404s" is not
/// a question that can be asked of this host any more. What remains to prove about StartAsync is the
/// registration file it writes (which phase 4's lifecycle machinery reads) and the identity handoff.
/// The prompt-delivery-failures fleet view died with its route by the mission plan's ruling: the log
/// file is the durable record, and each session's row still carries its own counts.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private string _instancesDir = null!;

    public ControlApiHostTests()
    {
        // Isolate the machine-global director root so the host writes its state into a temp root -
        // independent of whatever gateway the test machine happens to have configured.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosttests-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _instancesDir = Path.Combine(_root, "instances-isolated");
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _instancesDir);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Registration_file_exists_after_start_and_advertises_no_endpoint()
    {
        var f = Path.Combine(_instancesDir, $"{_host.DirectorId}.json");
        Assert.True(File.Exists(f), $"Registration file should exist at {f}");

        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        var root = doc.RootElement;
        Assert.Equal(_host.DirectorId, root.GetProperty("DirectorId").GetString());

        // Phase 4's lifecycle machinery certifies the registration's author by these two fields;
        // they are what makes the file usable without a socket.
        Assert.Equal(Environment.ProcessId, root.GetProperty("Pid").GetInt32());
        Assert.True(root.TryGetProperty("StartedAt", out _));

        // And the point of phase 5: the registration names NO control endpoint. Empty, not absent,
        // so old readers deserialize cleanly - but never an address, because nothing answers one.
        Assert.Equal("", root.GetProperty("ControlEndpoint").GetString());
    }

    [Fact]
    public async Task Registration_file_is_deleted_on_stop()
    {
        // A second, independent host so the claim does not ride on fixture teardown ordering.
        using var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _instancesDir);
        await host.StartAsync();
        var f = Path.Combine(_instancesDir, $"{host.DirectorId}.json");
        Assert.True(File.Exists(f));

        await host.StopAsync();

        Assert.False(File.Exists(f), "a stopped Director must not leave a registration behind");
    }

    [Fact]
    public void Host_start_publishes_director_identity_to_session_manager()
    {
        Assert.Equal(_host.DirectorId, _sm.DirectorId);
    }
}

/// <summary>
/// Regression for the "session never turns red when the Control API can't bind" bug
/// (2026-06-15). The per-session state services (SessionStatusWingman + TerminalStateDetector)
/// used to start mid-StartAsync, AFTER the port allocation, and a bind failure aborted StartAsync
/// before those services ran - so the desktop badge (Session.StatusColor) froze on its last colour
/// and a silent session could never flip to the red "needs you" state. The port is gone
/// (Remove-the-network-port mission, phase 5), but the ordering property it taught - state services
/// first, before anything in StartAsync that can fail - is kept, and these tests still prove the
/// badge pipeline runs with nothing else of the host started.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionStateServicesDecouplingTests
{
    [Fact]
    public async Task StateServices_DriveBadgeColour_WithoutStartAsync()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString());

        // Start the state services directly; StartAsync never runs.
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
            // StatusColor; if it is running, the badge follows.
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
    public async Task StartSessionStateServices_IsIdempotent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString());
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

// Remove-the-network-port mission, phase 5, tests removed WITH THEIR SUBJECT rather than adapted:
//
//   * ControlApiHostEphemeralFallbackTests (issue #697): the ephemeral-port fallback existed so a
//     Director whose fixed range [7879..7898] was exhausted did not lose its Control API. There is
//     no Control API, no fixed range, no port allocator and no fallback - the whole failure mode
//     ("all ports busy") cannot occur on a host that binds nothing.
//   * ReportStartupFailure_SetsErrorAndRaisesEvent: the startup-failure surface existed to tell the
//     desktop a BIND failed. Nothing binds; the member is gone with the indicator that rendered it.
//   * Healthz / prompt-delivery-failures / "shutdown is no longer a route": see the class comment
//     at the top of this file.

/// <summary>
/// Issue #846: the session-number backfill is wired into production. These tests prove the two
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

        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: Path.Combine(_root, "instances-isolated"));
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
