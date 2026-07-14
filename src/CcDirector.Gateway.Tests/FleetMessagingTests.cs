using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the pure message-framing helper used by fleet session-to-session
/// messaging (issue #705). Pure and machine-independent.
///
/// Gateway Cleanup mission (CUT RESTORATION, SB-4a): the fleet-relay ENDPOINTS that use this helper
/// (the Director's POST /fleet/send, /fleet/ask, GET /fleet/sessions, POST /fleet/spawn) were briefly
/// deleted at the cut, then RESTORED to the Director's LOOPBACK floor (Phase-4-DEFERRED, loopback +
/// outbound-relay only, so the inbound port stays closed) - cc-devthrottle drives them on the local
/// Director to coordinate the fleet. Their request-validation contract is re-pinned in
/// <see cref="FleetMessagingEndpointTests"/> below. Only /fleet/broadcast is NOT restored (Gateway-native +
/// Hub-gated, issue #1229), so its test is not re-added. This framing helper stands on its own.
/// </summary>
public sealed class FleetMessagingFramingTests
{
    [Fact]
    public void ShortId_truncates_to_eight_characters()
    {
        Assert.Equal("4c810000", FleetMessaging.ShortId("4c810000-1111-2222"));
        Assert.Equal("abc", FleetMessaging.ShortId("abc"));
        Assert.Equal("", FleetMessaging.ShortId(null));
    }

    [Fact]
    public void BuildFramedMessage_WithName_includes_name_machine_id_and_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "4c810000-1111-2222-3333-444444444444", "feature-work", "machine-A", "run the tests");

        Assert.StartsWith("Message ", framed);
        Assert.Contains("[message from feature-work (machine-A), id 4c810000]", framed);
        Assert.Contains("run the tests", framed);
        Assert.Contains("(to reply: cc-devthrottle message send 4c810000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithIdButNoName_uses_generic_session_header_with_reply()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "9b2f0000-aaaa-bbbb-cccc-dddddddddddd", null, "machine-B", "hello");

        Assert.Contains("[message from session 9b2f0000 (machine-B)]", framed);
        Assert.Contains("(to reply: cc-devthrottle message send 9b2f0000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithNoSender_is_anonymous_and_has_no_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(null, null, "machine-C", "broadcast text");

        Assert.Contains("[message from another session]", framed);
        Assert.Contains("broadcast text", framed);
        Assert.DoesNotContain("to reply:", framed);
    }

    [Fact]
    public void BuildFramedMessage_IsSingleLine_soItDeliversInlineToEveryAgent()
    {
        // A multi-line frame is routed through the @-temp-file delivery path that some agents (e.g. Pi)
        // do not expand, so they would see the file reference instead of the message. The frame - even
        // for a multi-line body - must collapse to a single line so it is typed inline.
        var framed = FleetMessaging.BuildFramedMessage(
            "4c810000-1111-2222-3333-444444444444", "asker", "machine-A",
            "Reply with\nexactly\nGREEN-42");

        Assert.DoesNotContain("\n", framed);
        Assert.Contains("Reply with exactly GREEN-42", framed); // body newlines collapsed to spaces
    }
}

/// <summary>
/// Gateway Cleanup mission (CUT RESTORATION, SB-4a): endpoint validation tests for the /fleet/* relay routes
/// (issue #705), restored to the Director's LOOPBACK floor. These assert the deterministic request-validation
/// that runs BEFORE any Gateway interaction (so they pass whether or not this machine has a Gateway), plus the
/// no-Gateway loopback outcomes (a local /fleet/sessions listing; an unknown target with no Gateway is a clear
/// 404, never a silent drop). The richer relay behavior is verified by live proof against a running Director.
/// The /fleet/broadcast route is NOT restored (Hub-gated, issue #1229), so it is not tested here.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetMessagingEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public FleetMessagingEndpointTests()
    {
        // Isolate CC_DIRECTOR_ROOT to a fresh temp dir so NO Gateway is configured for this Director. That
        // makes the no-Gateway outcomes deterministic (an unknown target -> a clear 404, a remote spawn -> a
        // loud 502) instead of depending on whatever Gateway the test machine happens to have configured.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-fleetmsg-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
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
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ===== /fleet/send validation =====

    [Fact]
    public async Task Fleet_send_missing_target_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send", new FleetSendRequest { ToSessionId = "", Text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_send_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send", new FleetSendRequest { ToSessionId = "not-a-guid", Text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_send_empty_text_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send",
            new FleetSendRequest { ToSessionId = Guid.NewGuid().ToString(), Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_send_unknown_target_noGateway_returns_404_notASilentDrop()
    {
        // A valid, well-formed target that this Director does not own, with no Gateway configured: the route
        // must FAIL LOUD (404), never silently accept-and-drop.
        var resp = await _client.PostAsJsonAsync("fleet/send",
            new FleetSendRequest { ToSessionId = Guid.NewGuid().ToString(), Text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== /fleet/ask validation =====

    [Fact]
    public async Task Fleet_ask_missing_target_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest { ToSessionId = "", Question = "q?" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest { ToSessionId = "not-a-guid", Question = "q?" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_empty_question_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask",
            new FleetAskRequest { ToSessionId = Guid.NewGuid().ToString(), Question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_unknown_target_noGateway_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask",
            new FleetAskRequest { ToSessionId = Guid.NewGuid().ToString(), Question = "q?" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== /fleet/sessions + /fleet/spawn loopback =====

    [Fact]
    public async Task Fleet_sessions_noGateway_returns200_withThisDirectorsSessions()
    {
        // Standalone (no Gateway): the route serves this Director's own live sessions - an empty list is a
        // valid 200 for a freshly-started Director with no sessions.
        var resp = await _client.GetAsync("fleet/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sessions = await resp.Content.ReadFromJsonAsync<List<SessionDto>>();
        Assert.NotNull(sessions);
    }

    [Fact]
    public async Task Fleet_spawn_missing_repo_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/spawn", new NewSessionRequest { RepoPath = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_spawn_remoteMachine_noGateway_failsLoud_502_neverLocalFallback()
    {
        // A remote machine target with no Gateway configured must FAIL LOUD (502), never fall back to a
        // local spawn (no-fallback rule).
        var resp = await _client.PostAsJsonAsync("fleet/spawn",
            new NewSessionRequest { RepoPath = @"D:\ReposFred\devthrottle", Machine = "some-other-machine" });
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // ===== /fleet/rename validation (issue #1490 - route restored to the tunnel-only floor) =====

    // A missing route returns 404, so a 400 from the HANDLER is itself the proof that /fleet/rename is
    // registered again - the exact gap #1490 was: the CLI's rename 404'd because the route was gone.
    [Fact]
    public async Task Fleet_rename_missing_target_returns_400_provingRouteExists()
    {
        var resp = await _client.PostAsJsonAsync("fleet/rename", new FleetRenameRequest { ToSessionId = "", Name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_rename_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/rename", new FleetRenameRequest { ToSessionId = "not-a-guid", Name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_rename_unknown_target_noGateway_returns_404()
    {
        // A well-formed target this Director does not own, with no Gateway: fail loud (404), never a silent no-op.
        var resp = await _client.PostAsJsonAsync("fleet/rename",
            new FleetRenameRequest { ToSessionId = Guid.NewGuid().ToString(), Name = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== /fleet/role validation (the set-role verb restored to the tunnel-only floor) =====

    // A missing route returns 404, so a 400 from the HANDLER is itself the proof that /fleet/role is
    // registered - the gap was that POST /sessions/{sid}/role was deleted in the tunnel-only cut, leaving a
    // running session stuck with the role it was born with.
    [Fact]
    public async Task Fleet_role_missing_target_returns_400_provingRouteExists()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role", new FleetRoleRequest { ToSessionId = "", Role = "Architect" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_role_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role",
            new FleetRoleRequest { ToSessionId = "not-a-guid", Role = "Architect" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // An unknown role must be REJECTED, not silently dropped - a mistyped --role that quietly did nothing is
    // exactly how a session ends up with the wrong role and nobody notices.
    [Fact]
    public async Task Fleet_role_unknown_role_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role",
            new FleetRoleRequest { ToSessionId = Guid.NewGuid().ToString(), Role = "Overlord" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Validation order matters: an unknown role is rejected as a bad request BEFORE the session lookup, so a
    // typo is reported as a typo rather than as "session not found".
    [Fact]
    public async Task Fleet_role_unknown_role_beats_unknown_session_inValidationOrder()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role",
            new FleetRoleRequest { ToSessionId = Guid.NewGuid().ToString(), Role = "Overlord" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain("not found", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // A well-formed target this Director does not own, with no Gateway to relay through: fail loud (404),
    // never a silent no-op. Matches /fleet/rename's contract exactly.
    [Fact]
    public async Task Fleet_role_unknown_target_noGateway_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role",
            new FleetRoleRequest { ToSessionId = Guid.NewGuid().ToString(), Role = "Architect" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // An EMPTY role is the documented "clear it" path, so it must pass validation and reach the session
    // lookup (404 here, no Gateway) rather than being rejected as a bad request alongside genuine typos.
    [Fact]
    public async Task Fleet_role_empty_role_isClearNotReject()
    {
        var resp = await _client.PostAsJsonAsync("fleet/role",
            new FleetRoleRequest { ToSessionId = Guid.NewGuid().ToString(), Role = "" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== /fleet/done validation (issue #1490 - the self-reap route restored to the floor) =====

    [Fact]
    public async Task Fleet_done_missing_target_returns_400_provingRouteExists()
    {
        var resp = await _client.PostAsJsonAsync("fleet/done", new FleetDoneRequest { ToSessionId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_done_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/done", new FleetDoneRequest { ToSessionId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_done_unknown_target_noGateway_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("fleet/done",
            new FleetDoneRequest { ToSessionId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== /fleet/broadcast validation (issue #1490 - "message send all", route was never built) =====

    // A 400 from the handler proves the route now exists (a missing route 404s) - the gap that made
    // `message send all` 404.
    [Fact]
    public async Task Fleet_broadcast_missing_text_returns_400_provingRouteExists()
    {
        var resp = await _client.PostAsJsonAsync("fleet/broadcast",
            new FleetBroadcastRequest { Text = "", FromSessionId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_broadcast_missing_sender_returns_400_soTheTeamCanBeResolved()
    {
        // Without the sender's id the team cannot be resolved, so a broadcast that would otherwise reach
        // "everyone" is refused up front rather than guessing.
        var resp = await _client.PostAsJsonAsync("fleet/broadcast",
            new FleetBroadcastRequest { Text = "hi", FromSessionId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_broadcast_senderNotInFleet_returns_404()
    {
        // Standalone Director with no sessions: the sender is not in the (empty) fleet, so its team cannot
        // be resolved - fail loud (404), never broadcast to a guessed set.
        var resp = await _client.PostAsJsonAsync("fleet/broadcast",
            new FleetBroadcastRequest { Text = "hi", FromSessionId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
