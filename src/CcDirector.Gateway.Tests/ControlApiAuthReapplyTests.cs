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
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", DirectorScopedToken.Mint(secret, ScopeNames.Admin));
        using var resp = await client.GetAsync("workspaces");
        return resp.StatusCode;
    }
}
