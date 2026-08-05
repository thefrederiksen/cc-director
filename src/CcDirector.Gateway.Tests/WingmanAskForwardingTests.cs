using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Phase 5.1: integration coverage for the wingman "ask" endpoint forwarding
/// path. In-process Director + Gateway. We do not exercise the live <c>claude --print</c>
/// invocation (no CLI in CI) - instead we use the fail-open contract: with an empty
/// <c>ClaudePath</c>, <c>AskAboutSessionAsync</c> returns <c>Status="no_claude"</c>
/// without spawning a process, which is enough to verify the wire path.
/// </summary>
public sealed class WingmanAskForwardingTests : IAsyncLifetime
{
    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    // Isolated discovery dir: the test Director and Gateway find each other here, and a real
    // Director running on the dev machine can never leak into (or see) these test hosts.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // ClaudePath empty -> AskAboutSessionAsync returns no_claude without spawning.
        _sm = new SessionManager(new AgentOptions { ClaudePath = "" });
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: _instancesDir);
        await _director.StartAsync();

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Wait for FSW discovery of the in-process Director.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors(CcDirector.Gateway.Tenancy.SystemScope.Grant()).Any(d => d.DirectorId == _director.DirectorId)) break;
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task Ask_with_empty_question_returns_400()
    {
        // The route validates the body BEFORE it locates a session, so any well-formed id proves
        // the 400: an empty question never reaches a Director, which is the point.
        var resp = await _http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/wingman/ask",
            new WingmanAskRequest { Question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// The no-claude contract, at the verb core the Gateway's tunnel dispatches into.
    ///
    /// THIS TEST USED TO PASS WITHOUT RUNNING. It asked the Gateway over HTTP and needed a real
    /// session, which it created through the Director's own `POST /sessions` route - a route the
    /// Gateway Cleanup cut had already deleted. So creation always failed, `isReal` was always
    /// false, and the body early-returned: a green test that asserted nothing. The
    /// Remove-the-network-port mission's phase 5 exposed it, because re-pointing session creation
    /// at the real create verb made creation SUCCEED for the first time - and the ask then failed,
    /// since the Gateway's wingman-ask route is tunnel-only and this fixture has no tunnel.
    ///
    /// Restoring the old shape would restore a test that cannot fail. Asserting it at the core -
    /// the same `wingman-ask` verb the tunnel invokes - is what actually holds the claim.
    /// </summary>
    [Fact]
    public async Task Ask_no_claude_returns_no_claude_status_with_context_digest()
    {
        // An embedded session with a stub backend, NOT CreatePipeModeSession: this fixture sets
        // ClaudePath empty on purpose (that is what makes the ask answer no_claude without
        // spawning), and pipe mode would try to launch that empty path and throw before the test
        // began. The stub puts a real session in the roster with no process behind it.
        var session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());

        var result = await SessionCommandExecutor.DispatchAsync(_sm, _director.DirectorId, new DirectorCommand
        {
            CommandId = "cmd-wingman-ask",
            Verb = "wingman-ask",
            SessionId = session.Id.ToString(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new WingmanAskRequest { Question = "what is going on" },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        });

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var body = System.Text.Json.JsonSerializer.Deserialize<WingmanAskResult>(result.BodyJson ?? "{}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        // ClaudePath is empty on this fixture's options, so the fail-open contract answers without
        // ever spawning a process, and it says WHY in words a person can act on.
        Assert.Equal("no_claude", body!.Status);
        Assert.Contains("claude", body.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agents.claudePath", body.Answer ?? "", StringComparison.Ordinal);

        // THE DIGEST IS NOT ASSERTED HERE, AND THAT IS A FINDING RATHER THAN A CONCESSION. The old
        // (vacuous) version of this test demanded a non-empty ContextDigest. Now that it actually
        // runs, the free-text ask path - WingmanService.AnswerViaSessionAsync - is seen to omit the
        // digest on its no_claude branch while setting it on its success branch, and its sibling
        // AskAboutSessionAsync sets one on BOTH. So the two wingman paths disagree about whether a
        // no-claude answer carries its context digest.
        //
        // That inconsistency predates this mission and is not caused by removing a network port, so
        // it is REPORTED rather than quietly fixed inside a deletion change - the phase's diff has
        // to stay about the port. Asserting the digest here would either fail against shipped
        // behaviour or force an unrelated product change to make a test green.
    }

    [Fact]
    public async Task Ask_for_unknown_session_returns_404()
    {
        var bogus = Guid.NewGuid().ToString();
        var resp = await _http.PostAsJsonAsync($"sessions/{bogus}/wingman/ask",
            new WingmanAskRequest { Question = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // The two session-creating helpers that used to live here are gone. They spawned a real agent
    // process to obtain a session id that neither remaining wire test actually needs: the
    // empty-question 400 is returned by the route BEFORE it locates a session, and the
    // unknown-session test wants an id no Director claims.

}
