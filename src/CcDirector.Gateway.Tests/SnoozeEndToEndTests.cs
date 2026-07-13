using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof of the Snooze Length Phase 1 round-trip, entirely in-process (Snooze Length
/// mission, docs/architecture/snooze-length-mission-2026-07-11.md). It boots a REAL
/// <see cref="GatewayHost"/> on an ephemeral loopback port (no Tailscale front door -
/// CC_GATEWAY_NO_TAILSCALE=1), drives the REAL POST /sessions/{sid}/hold, and asserts the whole
/// state machine end to end:
///
///   1. Holding a session through the Gateway records a snooze-until at now + the per-user default.
///   2. Once that time passes, the /sessions fold returns the session to "needs you" on its own -
///      no client, no Director action.
///   3. The wired watchdog nudges the live Director off hold and clears the entry once the Director
///      confirms it is no longer held.
///   4. A pending snooze survives a full Gateway restart (re-armed from the on-disk registry).
///   5. A snooze survives the Director itself dying (dead-man's switch, served from the cached roster).
///
/// Gateway Cleanup mission (the cut): the roster is the PUSH store and every Director verb rides THE
/// TUNNEL. Each Director registers UNREACHABLE, connects the stream, PUSHES its sessions, and answers
/// the snooze watchdog's read/write verbs ("snapshot" for the raw OnHold read, "hold" for the nudge)
/// and the hold endpoint's "hold" forward over the tunnel. Because the advertised endpoint is dead, a
/// working result proves the tunnel. A DEAD Director drops its stream, so its sessions leave the push
/// store; the last-known roster (FleetRosterCache) still carries the session so the expiry overlay
/// returns it to "needs you" on its own.
///
/// CC_DIRECTOR_ROOT is redirected to a temp dir so the snooze-default setting and the registry file
/// live in an isolated store, never the real one. In the "DirectorRoot" collection so it never runs
/// alongside other root-redirecting tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SnoozeEndToEndTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevNoTailscale;
    private readonly string _instancesDir;
    private readonly string _snoozePath;

    private GatewayHost _gw = null!;
    private HttpClient _http = null!;
    private readonly List<SnoozeFake> _fakes = new();

    public SnoozeEndToEndTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevNoTailscale = Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE");
        _root = Path.Combine(Path.GetTempPath(), "cc-snooze-e2e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // Never touch the Tailscale Serve front door from a test (leftover-front-door hazard).
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        _instancesDir = Path.Combine(_root, "instances");
        _snoozePath = Path.Combine(_root, "snooze", "snooze.json");
    }

    public async Task InitializeAsync()
    {
        (_gw, _http) = await StartGatewayAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
        await _gw.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", _prevNoTailscale);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    // Boot a real Gateway over the SAME isolated instances dir + snooze file, so a second call (after
    // disposing the first) is a genuine Gateway restart that must re-arm the persisted registry.
    private async Task<(GatewayHost, HttpClient)> StartGatewayAsync()
    {
        var gw = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: _snoozePath,
            streamMode: true);
        await gw.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gw.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return (gw, http);
    }

    [Fact]
    public async Task Hold_records_the_default_snooze_and_the_fold_returns_it_to_needs_you_once_it_expires()
    {
        // The default snooze length is set through the REAL setting endpoint (one minute - the floor).
        var putResp = await _http.PutAsJsonAsync("gateway/snooze-default", new { minutes = 1 });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var fake = await StartFakeAsync("s1", onHold: false);

        // Drive the REAL Gateway hold endpoint - exactly what the phone/cockpit Snooze button calls.
        var holdResp = await _http.PostAsJsonAsync("sessions/s1/hold", new HoldRequest { OnHold = true });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // The Director now reports it held (the hold verb rode the tunnel), and the Gateway recorded a
        // snooze-until at ~now + 1 minute. The Director pushes its new held state up the stream.
        Assert.True(fake.CurrentOnHold("s1"));
        await fake.PushAsync();

        var entry = Assert.Single(_gw.SnoozeRegistry.Entries());
        Assert.Equal("s1", entry.SessionId);
        var minutesOut = entry.SnoozeUntilUtc - DateTime.UtcNow;
        Assert.InRange(minutesOut.TotalSeconds, 45, 75); // one minute, generous tolerance

        // Still in the future -> the roster shows it parked (grey / onHold).
        var parked = await GetSession("s1");
        Assert.True(parked.OnHold);
        Assert.Equal("onHold", parked.TriageBucket);

        // Advance the clock deterministically by re-stamping the entry into the past (same as one minute
        // elapsing, without the wall-clock wait). Nothing else touches the session.
        _gw.SnoozeRegistry.Snooze("s1", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // On its own, with no client and no Director action, the fold returns it to "needs you".
        var returned = await GetSession("s1");
        Assert.False(returned.OnHold);                  // overlay flipped it
        Assert.Equal("red", returned.EffectiveColor);
        Assert.Equal("needsYou", returned.TriageBucket);
    }

    [Fact]
    public async Task Watchdog_nudges_the_live_director_off_hold_and_clears_once_confirmed()
    {
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s2", onHold: true); // already held on the Director
        // Arm an already-expired snooze directly (the record path is covered by the test above).
        _gw.SnoozeRegistry.Snooze("s2", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // First sweep: sees the Director still holding + expired -> nudges it off hold, keeps the entry.
        // ReadOnHold rides the "snapshot" verb; the nudge rides the "hold" verb with OnHold=false.
        await _gw.RunSnoozeSweepOnceAsync();
        Assert.Contains(false, fake.HoldCalls("s2"));   // a hold=false was forwarded over the tunnel
        Assert.False(fake.CurrentOnHold("s2"));         // the Director applied it
        Assert.True(_gw.SnoozeRegistry.Contains("s2")); // entry KEPT until the Director confirms

        // Second sweep: the Director now reports not-held -> the entry is cleared.
        await _gw.RunSnoozeSweepOnceAsync();
        Assert.False(_gw.SnoozeRegistry.Contains("s2"));
    }

    [Fact]
    public async Task Early_return_before_expiry_clears_the_snooze_when_the_director_reports_not_held()
    {
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s3", onHold: true);
        _gw.SnoozeRegistry.Snooze("s3", DateTime.UtcNow.AddMinutes(30), fake.DirectorId); // NOT expired

        // The user drove the session again (issue #470): the Director reports it no longer held.
        fake.SetOnHold("s3", false);

        await _gw.RunSnoozeSweepOnceAsync();
        Assert.False(_gw.SnoozeRegistry.Contains("s3")); // the snooze just clears
    }

    [Fact]
    public async Task A_pending_snooze_survives_a_full_gateway_restart()
    {
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s4", onHold: true);
        // An already-expired snooze written to disk. It must still fire after a restart.
        _gw.SnoozeRegistry.Snooze("s4", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // Restart the Gateway: dispose the old host, boot a fresh one over the SAME on-disk registry.
        _http.Dispose();
        await _gw.StopAsync();
        (_gw, _http) = await StartGatewayAsync();

        // The pending snooze was re-armed from disk.
        Assert.True(_gw.SnoozeRegistry.Contains("s4"));

        // And it still drives the fold: the (still-running) Director reconnects its tunnel to the fresh
        // Gateway and re-pushes, and the expired snooze returns the session to "needs you" on its own.
        await fake.ReconnectAsync(_gw);
        var returned = await GetSession("s4");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
    }

    [Fact]
    public async Task A_dead_director_snooze_still_returns_to_needs_you_from_the_cached_roster()
    {
        // THE mission scenario: a session is snoozed, then its whole Director dies, and the snooze must
        // still bring it back - the dead-man's switch. The timer lives on the Gateway, and the last-known
        // roster (FleetRosterCache) keeps the session visible, so the fold's expiry overlay returns it to
        // "needs you" on its own even though the Director is gone.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s5", onHold: true); // held on the Director

        // Prime the last-known-good roster while the Director is still alive.
        var alive = await GetSession("s5");
        Assert.True(alive.OnHold); // shown as snoozed while held (no expiry entry yet)

        // Arm an already-expired snooze, then the Director DIES (its stream drops).
        _gw.SnoozeRegistry.Snooze("s5", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);
        await fake.DisconnectAsync();
        _fakes.Remove(fake);

        // The Gateway can no longer reach the Director (its sessions left the push store), but the cached
        // roster still carries s5 and the expiry overlay returns it to "needs you" - not lost by snoozing.
        var returned = await GetSession("s5");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
        Assert.True(returned.SnoozeExpired);
    }

    // ---- helpers ----

    private async Task SetDefaultMinutes(int minutes)
    {
        var resp = await _http.PutAsJsonAsync("gateway/snooze-default", new { minutes });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<SessionDto> GetSession(string sid)
    {
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions", JsonOpts) ?? new();
        return Assert.Single(sessions, s => s.SessionId == sid);
    }

    private async Task<SnoozeFake> StartFakeAsync(string sid, bool onHold)
    {
        var fake = new SnoozeFake(sid, onHold);
        await fake.ConnectAsync(_gw, Token);
        _fakes.Add(fake);
        return fake;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// A tunnel-connected stand-in Director for the snooze flow: it registers UNREACHABLE, pushes its
    /// sessions into the roster, and answers the two verbs the flow touches - "snapshot" (the raw
    /// per-session OnHold read the watchdog does) and "hold" (the park/un-park write) - with a MUTABLE
    /// per-session OnHold the hold verb updates, so the Gateway's forward is observable. MachineName is
    /// this machine so the sweep treats it as reachable. It can reconnect to a fresh Gateway after a
    /// restart under the SAME director id, so the persisted snooze still addresses it.
    /// </summary>
    private sealed class SnoozeFake : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; } = Environment.MachineName;

        private readonly object _gate = new();
        private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<bool>> _holdCalls = new(StringComparer.Ordinal);
        private FakeTunnelDirector? _director;

        public SnoozeFake(string sid, bool onHold)
        {
            _sessions[sid] = new SessionDto
            {
                SessionId = sid,
                Agent = "ClaudeCode",
                RepoPath = "repo",
                ActivityState = "WaitingForInput",
                Status = "Running",
                StatusColor = "red",
                OnHold = onHold,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            _holdCalls[sid] = new List<bool>();
        }

        public bool CurrentOnHold(string sid) { lock (_gate) return _sessions[sid].OnHold; }
        public IReadOnlyList<bool> HoldCalls(string sid) { lock (_gate) return _holdCalls[sid].ToList(); }
        public void SetOnHold(string sid, bool value) { lock (_gate) _sessions[sid].OnHold = value; }

        // Register UNREACHABLE + connect the stream + push the current snapshot.
        public async Task ConnectAsync(GatewayHost gw, string token)
        {
            _director = await FakeTunnelDirector.StartAsync(gw, token, DirectorId, MachineName, Dispatch);
            await PushAsync();
        }

        // After a Gateway restart the OLD stream is dead; reconnect the same director id to the fresh host.
        public async Task ReconnectAsync(GatewayHost gw)
        {
            await DisconnectAsync();
            await ConnectAsync(gw, Token);
        }

        public Task PushAsync()
        {
            SessionDto[] snap;
            lock (_gate) snap = _sessions.Values.Select(Clone).ToArray();
            return _director!.PushSnapshotAsync(snap);
        }

        public async Task DisconnectAsync()
        {
            if (_director is not null)
            {
                await _director.DisposeAsync();
                _director = null;
            }
        }

        private DirectorCommandResult Dispatch(DirectorCommand cmd)
        {
            switch (cmd.Verb)
            {
                case "snapshot":
                    lock (_gate)
                        return _sessions.TryGetValue(cmd.SessionId, out var s)
                            ? FakeTunnelDirector.Ok(Clone(s))
                            : DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session");
                case "hold":
                {
                    var req = JsonSerializer.Deserialize<HoldRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson) ?? new HoldRequest();
                    lock (_gate)
                    {
                        if (!_sessions.TryGetValue(cmd.SessionId, out var s))
                            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session");
                        s.OnHold = req.OnHold;
                        _holdCalls[cmd.SessionId].Add(req.OnHold);
                        return FakeTunnelDirector.Ok(new HoldResponse { OnHold = s.OnHold });
                    }
                }
                default:
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            }
        }

        private static SessionDto Clone(SessionDto s) => new()
        {
            SessionId = s.SessionId,
            Agent = s.Agent,
            RepoPath = s.RepoPath,
            ActivityState = s.ActivityState,
            Status = s.Status,
            StatusColor = s.StatusColor,
            OnHold = s.OnHold,
            CreatedAt = s.CreatedAt,
            LastActivityAt = s.LastActivityAt,
        };

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
