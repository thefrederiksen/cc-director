using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Instances;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Remove-the-network-port mission, phase 3: the three session-hook routes ARE NOT THERE.
///
/// This is the phase's deletion proof, and it is a route-level one on purpose. A grep of
/// ControlEndpoints.cs would only tell you what that file says; this asks the running Director, over
/// real HTTP, on the port it really binds.
///
/// TWO THINGS MAKE THE ANSWER BELIEVABLE, and the mission learned both the hard way.
///
/// First, the probe holds a credential the Director ACCEPTS. In phase 2 an earlier probe used an invalid
/// credential and got 401 for everything - including routes that still existed - which is an
/// authentication refusal standing in for absence and proves nothing. An admin credential means a 404 is
/// the router saying there is no such path.
///
/// Second, there are POSITIVE CONTROLS: authenticated routes that still exist and answer something other
/// than 404 on this same host with this same client. Without them, a host that failed to map anything at
/// all - or a credential the host had started rejecting - would produce a clean sweep of 404s and read as
/// a successful deletion.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionHookRoutesAreGoneTests : IAsyncLifetime
{
    private SessionManager _sm = null!;
    private ControlApiHost _host = null!;
    private HttpClient _client = null!;
    private Session _session = null!;
    private readonly string _root;
    private readonly string? _prevRoot;

    public SessionHookRoutesAreGoneTests()
    {
        // A fresh temp root, so the accepted secret is this root's own token rather than whatever fleet
        // token the machine running the suite happens to carry.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-routes-gone-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = DirectorTestClient.Admin(port);

        // A session that really is on the roster, so a 404 cannot be explained away as "no such session".
        _session = new Session(
            Guid.NewGuid(),
            repoPath: Path.GetTempPath(),
            workingDirectory: Path.GetTempPath(),
            claudeArgs: null,
            backend: new IdleStubBackend(),
            claudeSessionId: "launch-time-id",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "routes-gone-test",
            customColor: null);
        _session.MarkRunning();
        _sm.AdoptSession(_session);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The detector, validated before it is trusted: authenticated routes that DO exist answer something
    /// other than 404 through this client. If this goes red, every 404 below means nothing.
    /// </summary>
    /// <remarks>
    /// <c>update/status</c> USED TO BE ONE OF THESE CONTROLS and is not any more: phase 4 deleted it
    /// along with the rest of the lifecycle surface. Left here it would 404 like the routes below, the
    /// detector would report itself broken, and the honest reading of that red would have been "no 404
    /// in this class can be trusted" - a true statement about a control that had simply gone stale.
    /// </remarks>
    [Theory]
    [InlineData("settings")]
    [InlineData("fleet/sessions")]
    public async Task Control_anAuthenticatedRouteThatStillExists_isNot404(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"the probe's own credential was refused on {path} with {(int)response.StatusCode} - " +
            "no 404 in this class can be trusted while that is true");
    }

    [Fact]
    public async Task The_two_fleet_preamble_routes_answer_404_for_a_session_that_exists()
    {
        using var plain = await _client.GetAsync($"sessions/{_session.Id}/fleet-preamble");
        using var hookOutput = await _client.GetAsync($"sessions/{_session.Id}/fleet-preamble-hook-output");

        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hookOutput.StatusCode);
    }

    [Fact]
    public async Task The_claude_hook_report_route_answers_404_for_a_session_that_exists()
    {
        using var response = await _client.PostAsync($"sessions/{_session.Id}/claude-hook",
            new StringContent("""{"session_id":"x","transcript_path":"/tmp/x.jsonl"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// And the pointer is untouched by that call, which is the property that matters rather than the
    /// status code: a 404 from a route that had nonetheless run its handler would be the worst of both.
    /// </summary>
    [Fact]
    public async Task A_post_to_the_deleted_claude_hook_route_does_not_move_the_pointer()
    {
        using var response = await _client.PostAsync($"sessions/{_session.Id}/claude-hook",
            new StringContent("""{"session_id":"hijack","transcript_path":"/tmp/hijack.jsonl"}""",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("launch-time-id", _session.ClaudeSessionId);
    }

    /// <summary>
    /// The replacement is running on this same host: the Director maintained a preamble file for the
    /// session it adopted, without being asked and without a route. So the routes are gone AND the thing
    /// that replaced them is up - the pair of facts the phase claims.
    /// </summary>
    [Fact]
    public void The_maintained_preamble_file_is_there_instead()
    {
        var path = SessionHookFiles.PreamblePathFor(_session.Id);

        Assert.True(File.Exists(path), $"the Director did not maintain a preamble file at {path}");
        Assert.Contains("hookSpecificOutput", File.ReadAllText(path));
    }

    /// <summary>
    /// Phase 4 of the same mission: the LIFECYCLE routes are gone too, proved with the same validated
    /// detector two tests up.
    ///
    /// This asks the question the other direction from the launcher's own tests. Those prove the
    /// launcher no longer NEEDS these routes; this proves the Director no longer OFFERS them - and both
    /// are needed, because a caller that stopped calling leaves a route that is merely unused, which is
    /// an opening rather than a capability. Shutdown in particular: while it existed, any local process
    /// that could reach the port and present a credential could stop the Director and every agent under
    /// it.
    /// </summary>
    [Fact]
    public async Task The_update_status_route_is_gone()
    {
        using var response = await _client.GetAsync("update/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_update_check_route_is_gone()
    {
        using var response = await _client.PostAsync("update/check", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_shutdown_route_is_gone()
    {
        using var response = await _client.PostAsync("shutdown", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class IdleStubBackend : Core.Backends.ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;
        public Core.Memory.CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
