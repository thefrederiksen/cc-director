using System.Net;
using System.Net.Http.Headers;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression test for the inspection's P1 finding: the auth middleware captured the accepted
/// secret ONCE at <c>StartAsync</c>, so a runtime gateway change - enroll, rotate, or disconnect,
/// anything that goes through <c>ReapplyGatewayAsync</c> - left the API accepting the OLD secret
/// and rejecting credentials derived from the NEW one until the Director was restarted. The CLI
/// and launcher resolve the secret fresh on every call, so after an enroll they were 401ed by
/// their own Director.
///
/// Isolation: CC_DIRECTOR_ROOT points at a fresh temp root, so the "machine" this test rotates a
/// token on is its own. The host and the minted credentials both resolve through that root, so the
/// test is independent of the real machine's config.json.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiAuthReapplyTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private int _port;

    public ControlApiAuthReapplyTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-auth-reapply-root-" + unique);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-auth-reapply-instances-" + unique);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: true, instancesDirectory: _instancesDir);
        _port = await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The pass-3 inspection's Finding 1: an ALREADY-RUNNING session's environment cannot be
    /// changed, so the session-child token stamped into it at launch is derived from the secret in
    /// force at launch time. When enrollment, rotation, or disconnect replaces the accepted secret
    /// at runtime, that live session's hooks keep presenting the old-secret child token; if the host
    /// verifies only against the new secret, that session's command line is refused 401 and the agent
    /// loses the fleet. The child credential must therefore keep working across ONE rotation - while
    /// full authority (admin, cli, and the raw secret itself) follows the current secret only, so a
    /// leaked old root credential still buys nothing.
    ///
    /// Probed on /fleet/buffer. The remove-the-network-port mission's phase 3 deleted the three
    /// /sessions/{sid} hook routes this used to probe - a session's hooks now read and write files and
    /// present no credential at all - so the buffer read is what a child credential is FOR now, and it
    /// is the right thing to hold the grace window to.
    /// </summary>
    [Fact]
    public async Task ReapplyGateway_AfterTokenChange_ALiveSessionsChildCredentialKeepsWorking()
    {
        var sessionId = Guid.NewGuid();
        var oldSecret = DirectorTestClient.RootSecret();

        // The credential a session launched under the old secret carries in its environment - the
        // exact value SessionCredentialSource minted at spawn. This cannot be re-issued after the
        // rotation: the process is already running.
        var launchTimeChildToken = DirectorScopedToken.Mint(oldSecret, ScopeNames.SessionChild, sessionId);

        // Sanity before the rotation: the hook's own-session calls pass the gate. (404/2xx are both
        // fine on a host with no such session - what is on trial is the credential gate.)
        Assert.False(await IsRefused(launchTimeChildToken, "GET", $"fleet/buffer?sessionId={sessionId}"),
            "before any rotation the child credential must pass the gate");

        // The real runtime rotation, through the same production path the enroll flow uses.
        var newSecret = "rotated-fleet-secret-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"token\":\"" + newSecret + "\"}}");
        await _host.ReapplyGatewayAsync();

        // The live session's own-session read keeps working.
        Assert.False(await IsRefused(launchTimeChildToken, "GET", $"fleet/buffer?sessionId={sessionId}"),
            "after the rotation the live session's child credential was refused - its command line is dead");
        // And so does the safe discovery set the command line's read verbs need.
        Assert.False(await IsRefused(launchTimeChildToken, "GET", "fleet/sessions"),
            "after the rotation the live session lost the fleet roster");

        // The grace is scoped to the child credential and does not widen its grant: the old child
        // token still cannot read another session or the settings.
        Assert.True(await IsRefused(launchTimeChildToken, "GET", $"fleet/buffer?sessionId={Guid.NewGuid()}"),
            "the old child credential must stay bound to its own session");
        Assert.True(await IsRefused(launchTimeChildToken, "GET", "settings"),
            "the old child credential must not reach the settings");

        // Full authority follows the CURRENT secret only: the old admin token, the old raw secret,
        // and an old cli token are all rejected; the new secret's admin token is accepted.
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeWith(oldSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeRaw(oldSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeRaw(DirectorScopedToken.Mint(oldSecret, ScopeNames.Cli)));
        Assert.Equal(HttpStatusCode.OK, await ProbeWith(newSecret));
    }

    /// <summary>The grace covers exactly ONE rotation: after a second rotation the oldest child
    /// credential is out of the window and is refused like any other stale credential.</summary>
    [Fact]
    public async Task ReapplyGateway_TwoRotations_TheOldestChildCredentialIsOut()
    {
        var sessionId = Guid.NewGuid();
        var firstSecret = DirectorTestClient.RootSecret();
        var firstChildToken = DirectorScopedToken.Mint(firstSecret, ScopeNames.SessionChild, sessionId);

        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"token\":\"second-" + Guid.NewGuid().ToString("N") + "\"}}");
        await _host.ReapplyGatewayAsync();
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"token\":\"third-" + Guid.NewGuid().ToString("N") + "\"}}");
        await _host.ReapplyGatewayAsync();

        Assert.True(await IsRefused(firstChildToken, "GET", $"fleet/buffer?sessionId={sessionId}"),
            "a child credential two rotations old must be refused - the grace window is one secret deep");
    }

    [Fact]
    public async Task ReapplyGateway_AfterTokenChange_AcceptsNewSecret_RejectsOldSecret()
    {
        // Startup posture: no gateway configured in the isolated root, so the accepted secret is
        // the root's own local token, and a credential derived from it is accepted.
        var oldSecret = DirectorTestClient.RootSecret();
        Assert.Equal(HttpStatusCode.OK, await ProbeWith(oldSecret));

        // The runtime gateway change: config.json now carries a fleet token - the shape the enroll
        // flow writes - and the settings path calls ReapplyGatewayAsync rather than restarting.
        var newSecret = "rotated-fleet-secret-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"token\":\"" + newSecret + "\"}}");
        await _host.ReapplyGatewayAsync();

        // A credential derived from the NEW secret is accepted, and one derived from the OLD
        // secret is rejected - the middleware honours the secret now in force, without a restart.
        Assert.Equal(HttpStatusCode.OK, await ProbeWith(newSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeWith(oldSecret));
    }

    /// <summary>GET a protected route presenting an admin credential derived from the given secret.</summary>
    private async Task<HttpStatusCode> ProbeWith(string secret)
        => await ProbeRaw(DirectorScopedToken.Mint(secret, ScopeNames.Admin));

    /// <summary>GET a protected route presenting the given value verbatim as the Bearer credential.</summary>
    private async Task<HttpStatusCode> ProbeRaw(string bearer)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await client.GetAsync("workspaces");
        return resp.StatusCode;
    }

    /// <summary>Whether the credential gate refused this call (401/403). 404/2xx both count as
    /// passed - the host under test has no real sessions, and the gate is what is on trial.</summary>
    private async Task<bool> IsRefused(string bearer, string method, string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(request);
        return resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }
}
