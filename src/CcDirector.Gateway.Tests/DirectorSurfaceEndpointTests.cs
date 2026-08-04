using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Validation of the Director's session-surface verb cores (#5 resize, #6 relink/git), at the seam
/// that survives: the shared <see cref="SessionCommandExecutor"/> the tunnel dispatches into.
///
/// Remove-the-network-port mission, phase 5: the loopback fixture (a real ControlApiHost on an
/// ephemeral port plus an admin HTTP client) is gone with the listener. The four HTTP tests that
/// posted at <c>sessions/{sid}/...</c> and expected 404 are folded into executor NotFound checks:
/// the HTTP versions were already only proving route-not-found once the routes moved to the tunnel,
/// which is a 404 any deleted router serves. The GET /workspaces and GET /history list routes died
/// with the listener and their tests with them - the workspace-history READ has no tunnel verb, and
/// the mission's phase 2 already recorded cc-history as dead in production (a pre-existing defect
/// filed separately, excluded from the phase pass mark).
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorSurfaceEndpointTests : IDisposable
{
    private const string DirectorId = "dir-surface-test";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly SessionManager _sm;

    public DirectorSurfaceEndpointTests()
    {
        // Isolate the machine-global director root so nothing reads the test machine's real config.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-surface-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _sm = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- #5 resize ----

    [Fact]
    public async Task Resize_rejects_nonpositive_dimensions()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, DirectorId,
            Command("resize", Guid.NewGuid().ToString(), new ResizeRequest { Cols = 0, Rows = 24 }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Resize_unknown_session_is_not_found()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, DirectorId,
            Command("resize", Guid.NewGuid().ToString(), new ResizeRequest { Cols = 80, Rows = 24 }));
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    // ---- #6 relink ----

    [Fact]
    public async Task Relink_rejects_empty_claude_session_id()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, DirectorId,
            Command("relink", Guid.NewGuid().ToString(), new RelinkRequest { ClaudeSessionId = "" }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Relink_unknown_session_is_not_found()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, DirectorId,
            Command("relink", Guid.NewGuid().ToString(), new RelinkRequest { ClaudeSessionId = "claude-xyz" }));
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    // ---- #6 git writes ----

    [Fact]
    public async Task Git_stage_unknown_session_is_not_found()
    {
        var result = await SessionCommandExecutor.DispatchAsync(_sm, DirectorId,
            Command("git-stage", Guid.NewGuid().ToString(), new GitPathsRequest()));
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    // ---- helpers ----

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DirectorCommand Command(string verb, string sessionId, object? payload = null) => new()
    {
        CommandId = "cmd-surface",
        Verb = verb,
        SessionId = sessionId,
        PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Json),
    };
}
