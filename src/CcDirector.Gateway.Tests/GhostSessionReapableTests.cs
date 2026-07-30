using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1019 - the card that nothing could remove. Three sessions were spawned, each call returned a
/// session id, and afterwards every tool denied they existed: the fleet listing, the Director's own
/// GET /fleet/sessions, and `session done` / `interrupt` / `buffer` all reported no such session, while one
/// card stayed on the rail, frozen. Restarting the Director was the only remedy.
///
/// The mechanism is not a lost session - it is a roster that omitted rows the Director was still holding.
/// GET /fleet/sessions is what every cc-devthrottle verb resolves an id against BEFORE it sends anything, so
/// a row missing from that one list cannot be named, and a row that cannot be named cannot be reaped. Two
/// independent omissions put rows there:
///
///   1. The listing filtered out ActivityState.Exited. A CRASHED session sits in exactly that state (a crash
///      was never modelled as its own state, it lives on Session.Crashed) and is deliberately KEPT in the
///      roster and on the rail (issue #959) so the user sees that work stopped. So the one row the user could
///      SEE and most needed to clear was the one row the CLI could not name. No Gateway required.
///   2. With a Gateway configured the listing served the relayed roster ONLY, and that relay silently drops
///      the sessions of a Director it cannot reach while still returning 200. A Director whose registration
///      was failing - which is what the report shows in the logs - therefore vanished from its own fleet
///      listing, live sessions and all.
///
/// The reap machinery on the other side was always willing: /fleet/done finds a session in the local store
/// and ReapPendingDeletions explicitly reaps an Exited one. Only the naming was impossible. These tests
/// prove the naming, then drive the reap to completion.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GhostSessionReapableTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public GhostSessionReapableTests()
    {
        // Isolate CC_DIRECTOR_ROOT so this Director has NO Gateway configured. That is the point: omission
        // one needs no Gateway at all, so the ghost must be reapable on a plain standalone Director.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ghost-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions())
        {
            // Reap a flagged session on the first sweep instead of 30s later, so the test drives the real
            // reaper rather than a shortened copy of it.
            DeletionGraceMs = 0,
        };
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A backend whose process exit can be driven, so the REAL crash decision runs.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // required by the interface, unused here
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        public void RaiseExit(int code)
        {
            _exited = true;
            ProcessExited?.Invoke(code);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Put a session in the live roster and crash it for real - nothing sets Session.Crashed by hand, the
    /// producer decides it from a genuine process exit, so this cannot pass on a fact production never emits.
    /// This is the exact state the report's frozen card was in: held in the store, shown on the rail, no
    /// process behind it.
    /// </summary>
    private Session AdoptCrashedSession()
    {
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        _sm.AdoptSession(session);

        session.ApplyTerminalActivityState(ActivityState.Working);
        backend.RaiseExit(1);   // the real producer decides crash-vs-clean

        Assert.True(session.Crashed);                                  // a genuine crash...
        Assert.Equal(ActivityState.Exited, session.ActivityState);      // ...in the state the filter dropped
        Assert.NotNull(_sm.GetSession(session.Id));                     // ...and still held, per issue #959
        return session;
    }

    private async Task<List<SessionDto>> GetFleetAsync()
    {
        var resp = await _client.GetAsync("fleet/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sessions = await resp.Content.ReadFromJsonAsync<List<SessionDto>>();
        Assert.NotNull(sessions);
        return sessions!;
    }

    // ===== Omission one: a held row must be nameable =====

    [Fact]
    public async Task CrashedSession_theDirectorStillHolds_isListedSoTheCliCanNameIt()
    {
        var ghost = AdoptCrashedSession();

        var fleet = await GetFleetAsync();

        // The whole of the report's second defect is this assertion. It failed before the fix: the roster
        // filtered ActivityState.Exited, so `cc-devthrottle session done <id>` could not resolve an id it
        // could not list and answered "No session matches" for a card plainly visible on the rail.
        Assert.Contains(fleet, s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CrashedSession_isListedAsCrashed_notSilentlyDressedUpAsLive()
    {
        // Listing it must not mean lying about it. The row carries the crash fact, so `session list` shows
        // a dead session as dead rather than implying there is an agent behind it.
        var ghost = AdoptCrashedSession();

        var row = (await GetFleetAsync())
            .Single(s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        Assert.True(row.Crashed);
        Assert.Equal("Exited", row.ActivityState);
    }

    // ===== And once nameable, it must actually go away =====

    [Fact]
    public async Task CrashedSession_isReapableEndToEnd_soTheCardCanBeCleared()
    {
        // The report's "there is no supported way to clear it": every tool denied the session, and restarting
        // the Director was the only remedy. This drives the whole supported route instead - list it, mark it
        // done through the real endpoint, let the real reaper sweep - and proves the row is gone afterwards.
        var ghost = AdoptCrashedSession();

        Assert.Contains(await GetFleetAsync(),
            s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        var done = await _client.PostAsJsonAsync("fleet/done",
            new FleetDoneRequest { ToSessionId = ghost.Id.ToString(), Reason = "ghost card, issue #1019" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await done.Content.ReadFromJsonAsync<FleetDoneResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Accepted);

        _sm.ReapPendingDeletions();

        // The reaper's removal is fire-and-forget, so wait for it rather than assuming it already ran.
        var removed = false;
        for (var i = 0; i < 40 && !removed; i++)
        {
            if (_sm.GetSession(ghost.Id) is null) { removed = true; break; }
            await Task.Delay(50);
        }
        Assert.True(removed, "the flagged ghost session was never removed from the roster");

        Assert.DoesNotContain(await GetFleetAsync(),
            s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownSession_stillFailsLoud_soListingHeldRowsDidNotWeakenTheGuard()
    {
        // The counterpart: widening the roster must not make the Director accept an id it has never seen.
        // A genuinely unknown target with no Gateway is still a clear 404, never a silent accept.
        var resp = await _client.PostAsJsonAsync("fleet/done",
            new FleetDoneRequest { ToSessionId = Guid.NewGuid().ToString(), Reason = "never existed" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

/// <summary>
/// Issue #1019, omission two: the pure fold that puts this Director's own sessions back into the roster
/// relayed from the Gateway. The relay drops an unreachable Director's sessions silently while still
/// returning 200, so a Director whose registration is failing disappears from its own fleet listing.
/// </summary>
public sealed class UnionOwnSessionsTests
{
    private static SessionDto Row(string id, string? name = null) =>
        new() { SessionId = id, Name = name ?? id };

    [Fact]
    public void OwnSessionsTheRelayOmitted_areRestored_andReported()
    {
        var fleet = new List<SessionDto> { Row("aaaa"), Row("bbbb") };
        var own = new List<SessionDto> { Row("cccc"), Row("dddd") };

        var (roster, restored) = ControlEndpoints.UnionOwnSessions(fleet, own);

        Assert.Equal(new[] { "aaaa", "bbbb", "cccc", "dddd" }, roster.Select(s => s.SessionId));
        // Reported, because a roster silently repaired is a roster that hides the registration failure that
        // needed repairing - the report's third defect was that nothing was written down anywhere.
        Assert.Equal(new[] { "cccc", "dddd" }, restored);
    }

    [Fact]
    public void SessionsTheRelayAlreadyKnows_keepTheGatewaysCopy_notTheLocalOne()
    {
        // The Gateway hands out the session numbers and stamps identity, so its row must survive untouched.
        // Preferring a local copy here would quietly overwrite that on every listing.
        var gatewayRow = Row("aaaa", name: "as the Gateway knows it");
        var localRow = Row("aaaa", name: "as this Director knows it");

        var (roster, restored) = ControlEndpoints.UnionOwnSessions(
            new List<SessionDto> { gatewayRow }, new List<SessionDto> { localRow });

        Assert.Single(roster);
        Assert.Same(gatewayRow, roster[0]);
        Assert.Empty(restored);
    }

    [Fact]
    public void IdMatching_isCaseInsensitive_soCasingNeverDuplicatesARow()
    {
        var (roster, restored) = ControlEndpoints.UnionOwnSessions(
            new List<SessionDto> { Row("AAAA-BBBB") }, new List<SessionDto> { Row("aaaa-bbbb") });

        Assert.Single(roster);
        Assert.Empty(restored);
    }

    [Fact]
    public void ARowWithNoSessionId_isNeverRestored_becauseNoCallerCouldAddressIt()
    {
        var (roster, restored) = ControlEndpoints.UnionOwnSessions(
            new List<SessionDto>(), new List<SessionDto> { new() { SessionId = "" }, Row("cccc") });

        Assert.Equal(new[] { "cccc" }, roster.Select(s => s.SessionId));
        Assert.Equal(new[] { "cccc" }, restored);
    }

    [Fact]
    public void AnEmptyRelayRoster_stillYieldsOurOwnSessions()
    {
        // The reported shape: the Gateway knew nothing about this Director, so the relay carried none of its
        // rows. Every one of them is ours to report.
        var (roster, restored) = ControlEndpoints.UnionOwnSessions(
            new List<SessionDto>(), new List<SessionDto> { Row("aaaa"), Row("bbbb") });

        Assert.Equal(new[] { "aaaa", "bbbb" }, roster.Select(s => s.SessionId));
        Assert.Equal(new[] { "aaaa", "bbbb" }, restored);
    }

    [Fact]
    public void NoOwnSessions_leavesTheRelayedRosterExactlyAsItCame()
    {
        var fleet = new List<SessionDto> { Row("aaaa"), Row("bbbb") };

        var (roster, restored) = ControlEndpoints.UnionOwnSessions(fleet, new List<SessionDto>());

        Assert.Equal(new[] { "aaaa", "bbbb" }, roster.Select(s => s.SessionId));
        Assert.Empty(restored);
    }
}
