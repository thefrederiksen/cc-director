using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof of the Snooze Length Phase 1 round-trip, entirely in-process (Snooze Length
/// mission, docs/architecture/snooze-length-mission-2026-07-11.md). This replaces the owner-clickable
/// live proof: it boots a REAL <see cref="GatewayHost"/> on an ephemeral loopback port (no Tailscale
/// front door - CC_GATEWAY_NO_TAILSCALE=1, so it never binds the 443 serve mapping), drives the REAL
/// POST /sessions/{sid}/hold, and asserts the whole state machine end to end:
///
///   1. Holding a session through the Gateway records a snooze-until at now + the per-user default.
///   2. Once that time passes, the /sessions fold returns the session to "needs you" on its own -
///      no client, no Director action.
///   3. The wired watchdog nudges the live Director off hold and clears the entry once the Director
///      confirms it is no longer held.
///   4. A pending snooze survives a full Gateway restart (re-armed from the on-disk registry).
///
/// CC_DIRECTOR_ROOT is redirected to a temp dir so the snooze-default setting and the registry file
/// live in an isolated store, never the real one. In the "DirectorRoot" collection so it never runs
/// alongside other root-redirecting tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SnoozeEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevNoTailscale;
    private readonly string _instancesDir;
    private readonly string _snoozePath;

    private GatewayHost _gw = null!;
    private HttpClient _http = null!;
    private readonly List<FakeDirector> _fakes = new();

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
        var gw = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: _snoozePath);
        await gw.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gw.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return (gw, http);
    }

    [Fact]
    public async Task Hold_records_the_default_snooze_and_the_fold_returns_it_to_needs_you_once_it_expires()
    {
        // The default snooze length is set through the REAL setting endpoint (one minute - the floor).
        var putResp = await _http.PutAsJsonAsync("gateway/snooze-default", new { minutes = 1 });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var fake = await StartFakeAsync("s1", onHold: false);
        await Register(fake);

        // Drive the REAL Gateway hold endpoint - exactly what the phone/cockpit Snooze button calls.
        var holdResp = await _http.PostAsJsonAsync("sessions/s1/hold", new HoldRequest { OnHold = true });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // The Director now reports it held, and the Gateway recorded a snooze-until at ~now + 1 minute.
        Assert.True(fake.CurrentOnHold("s1"));
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
        await Register(fake);
        // Arm an already-expired snooze directly (the record path is covered by the test above).
        _gw.SnoozeRegistry.Snooze("s2", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // First sweep: sees the Director still holding + expired -> nudges it off hold, keeps the entry.
        await _gw.RunSnoozeSweepOnceAsync();
        Assert.Contains(false, fake.HoldCalls("s2"));   // a hold=false was forwarded
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
        await Register(fake);
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
        await Register(fake);
        // An already-expired snooze written to disk. It must still fire after a restart.
        _gw.SnoozeRegistry.Snooze("s4", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // Restart the Gateway: dispose the old host, boot a fresh one over the SAME on-disk registry.
        _http.Dispose();
        await _gw.StopAsync();
        (_gw, _http) = await StartGatewayAsync();

        // The pending snooze was re-armed from disk.
        Assert.True(_gw.SnoozeRegistry.Contains("s4"));

        // And it still drives the fold: re-register the (still-running) Director and the expired snooze
        // returns the session to "needs you" on its own after the restart.
        await Register(fake);
        var returned = await GetSession("s4");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
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

    private async Task Register(FakeDirector fake)
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = fake.DirectorId,
            TailnetEndpoint = fake.BaseUrl,
            Pid = 1234,
            MachineName = fake.MachineName,
            User = "u",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<FakeDirector> StartFakeAsync(string sid, bool onHold)
    {
        var fake = new FakeDirector(sid, onHold);
        await fake.StartAsync();
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
    /// A minimal Director that serves the surface the snooze flow touches: GET /sessions (+ /{sid}) and
    /// POST /sessions/{sid}/hold, with a MUTABLE per-session OnHold the hold verb updates - so the
    /// Gateway's forward is observable and the next roster read reflects it. MachineName is this machine
    /// so the Gateway treats its loopback endpoint as same-machine reachable (the sweep can forward to it).
    /// </summary>
    private sealed class FakeDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; } = Environment.MachineName;
        public string BaseUrl { get; private set; } = "";

        private readonly object _gate = new();
        private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<bool>> _holdCalls = new(StringComparer.Ordinal);
        private WebApplication? _app;

        public FakeDirector(string sid, bool onHold)
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

        private SessionDto[] Snapshot()
        {
            lock (_gate)
                return _sessions.Values.Select(Clone).ToArray();
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

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "FakeDirector" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();
            _app.MapGet("/sessions", () => Results.Json(Snapshot()));
            _app.MapGet("/sessions/{sid}", (string sid) =>
            {
                lock (_gate)
                    return _sessions.TryGetValue(sid, out var s) ? Results.Json(Clone(s)) : Results.NotFound();
            });
            _app.MapPost("/sessions/{sid}/hold", async (string sid, HttpContext ctx) =>
            {
                var req = await JsonSerializer.DeserializeAsync<HoldRequest>(ctx.Request.Body, JsonOpts) ?? new HoldRequest();
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(sid, out var s)) return Results.NotFound();
                    s.OnHold = req.OnHold;
                    _holdCalls[sid].Add(req.OnHold);
                    return Results.Json(new HoldResponse { OnHold = s.OnHold });
                }
            });

            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
