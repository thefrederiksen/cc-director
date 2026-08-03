using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// What a SESSION KEY may and may not call (Remove-the-network-port mission, phase 1b).
///
/// The guard is the reason a session credential is worth having at all. Without it a session key is just
/// a differently-shaped account key: it would authenticate, and then reach the account surface exactly as
/// the Director's own key does, which is the widening this phase exists to avoid. So the tests that matter
/// most here are the REFUSALS - and they are written route by route rather than as one "denies something"
/// case, because an allow list that has quietly grown an entry fails by ALLOWING, and only a test that
/// names the thing it must refuse can see that.
/// </summary>
public sealed class SessionKeyGuardTests
{
    // ---------- The agent route set: what the fleet's command line needs ----------

    [Theory]
    [InlineData("GET", "/healthz")]
    [InlineData("GET", "/sessions")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/buffer")]
    [InlineData("GET", "/repositories")]
    [InlineData("GET", "/worktrees")]
    [InlineData("GET", "/directors")]
    [InlineData("GET", "/launchers")]
    [InlineData("GET", "/machines")]
    [InlineData("GET", "/machines/SOREN_NORTH/apps")]
    [InlineData("GET", "/machines/SOREN_NORTH/files")]
    [InlineData("GET", "/missions")]
    [InlineData("GET", "/missions/m-123")]
    [InlineData("GET", "/cron/jobs")]
    [InlineData("GET", "/cron/jobs/cj_abc")]
    [InlineData("GET", "/gateway/snooze-presets")]
    [InlineData("GET", "/gateway/skills")]
    [InlineData("GET", "/gateway/skills/move-session")]
    [InlineData("GET", "/gateway/skills/move-session/body")]
    [InlineData("GET", "/gateway/skills/move-session/versions")]
    [InlineData("GET", "/gateway/skills/move-session/versions/2")]
    [InlineData("GET", "/gateway/workflows/mission/instructions")]
    [InlineData("GET", "/gateway/workflow-runs")]
    [InlineData("GET", "/gateway/workflow-runs/run-9")]
    public void The_read_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    [Theory]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/prompt")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/interrupt")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/hold")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/role")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/mission")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/request-deletion")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/compact-context")]
    [InlineData("PATCH", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/fanout")]
    [InlineData("POST", "/missions")]
    [InlineData("POST", "/machines/SOREN_NORTH/sessions")]
    [InlineData("POST", "/machines/SOREN_NORTH/launch")]
    [InlineData("POST", "/gateway/skills/move-session/draft")]
    [InlineData("POST", "/gateway/skills/move-session/publish")]
    [InlineData("POST", "/gateway/workflows/mission/clone")]
    public void The_action_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    // ---------- The account surface: what a session key must never reach ----------

    [Theory]
    // Signing in and out, and the account's own data - the owner's identity, not an agent's.
    [InlineData("GET", "/account/status")]
    [InlineData("POST", "/account/sign-in")]
    [InlineData("POST", "/account/logout")]
    [InlineData("GET", "/account/credits")]
    [InlineData("GET", "/account/devices")]
    // Device enrollment and revocation: the credential system itself. A session key that could enroll a
    // device could mint itself an account-wide credential and step straight out of this guard.
    [InlineData("POST", "/devices/enroll")]
    [InlineData("POST", "/devices/enroll-signed-in")]
    [InlineData("DELETE", "/account/devices/some-device")]
    // Turning the Gateway or a Director off, or repointing its settings.
    [InlineData("POST", "/shutdown")]
    [InlineData("GET", "/directors/d-1/settings")]
    [InlineData("POST", "/directors/d-1/settings")]
    [InlineData("DELETE", "/directors/d-1/registration")]
    [InlineData("POST", "/directors/register")]
    // Somebody else's Director process lifecycle on another machine.
    [InlineData("POST", "/machines/SOREN_NORTH/director/stop")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart")]
    // Turning a fleet-wide capability off for everyone.
    [InlineData("POST", "/gateway/skills/move-session/disable")]
    [InlineData("POST", "/gateway/workflows/mission/enable")]
    // Deleting rather than reading.
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/cron/jobs/cj_abc")]
    // The diagnostics and reporting surfaces.
    [InlineData("GET", "/diag/loadmetrics")]
    [InlineData("GET", "/gateway/reports/morning")]
    // A route nobody has classified. THE DEFAULT IS DENY - this is the whole point of an allow list.
    [InlineData("GET", "/some/route/invented/next/year")]
    [InlineData("POST", "/some/route/invented/next/year")]
    public void The_account_surface_is_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must NOT be allowed");

    // ---------- The refusal itself ----------

    [Fact]
    public void A_refusal_names_the_method_and_the_path_it_refused()
    {
        var verdict = SessionKeyGuard.Check("POST", "/account/sign-in");

        Assert.False(verdict.Allowed);
        Assert.Contains("POST", verdict.Reason);
        Assert.Contains("/account/sign-in", verdict.Reason);
    }

    [Fact]
    public void An_allowed_request_carries_no_reason_to_read()
        => Assert.Equal("", SessionKeyGuard.Check("GET", "/sessions").Reason);

    // ---------- Shapes that could walk around it ----------

    [Fact]
    public void Case_does_not_open_a_route()
    {
        // ASP.NET matches a path case-insensitively, so a guard that compared ordinally would refuse
        // /Sessions here and then the router would serve it - a bypass consisting of one capital letter.
        Assert.True(SessionKeyGuard.Check("GET", "/Sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/Account/Sign-In").Allowed);
    }

    [Fact]
    public void A_trailing_slash_does_not_open_a_route()
    {
        Assert.True(SessionKeyGuard.Check("GET", "/sessions/").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/account/sign-in/").Allowed);
    }

    [Fact]
    public void The_method_decides_as_much_as_the_path()
    {
        // /sessions is a read an agent needs. The same path as a DELETE is not, and a guard that keyed on
        // the path alone would hand an agent the ability to wipe the roster.
        Assert.True(SessionKeyGuard.Check("GET", "/sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/sessions").Allowed);
    }

    [Fact]
    public void An_unknown_session_sub_route_is_refused_even_though_its_prefix_is_allowed()
    {
        // The sessions branch is an explicit list of verbs, NOT "anything under /sessions/{id}". If it were
        // the latter, every future per-session route - an upload, a settings write, a credential read -
        // would be open to every agent on the day it was added.
        Assert.False(SessionKeyGuard.Check("POST", "/sessions/11111111-1111-1111-1111-111111111111/upload-image").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/sessions/11111111-1111-1111-1111-111111111111/wingman").Allowed);
    }

    [Fact]
    public void A_missing_or_empty_path_is_refused()
    {
        Assert.False(SessionKeyGuard.Check("GET", null).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/").Allowed);
        Assert.False(SessionKeyGuard.Check(null, "/sessions").Allowed);
    }
}
