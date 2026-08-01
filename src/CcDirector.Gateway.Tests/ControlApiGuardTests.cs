using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The three request decisions the Control API makes before any handler runs, tested where they are
/// decided. The end-to-end hostile suite proves they are WIRED; these prove the rules themselves,
/// including the cases a live test would be clumsy to reach.
/// </summary>
public sealed class ControlApiGuardTests
{
    private const int Port = 7879;

    // ---------- The Host allowlist ----------

    [Theory]
    [InlineData("127.0.0.1:7879")]
    [InlineData("localhost:7879")]
    [InlineData("LOCALHOST:7879")]
    public void The_bound_loopback_authority_is_accepted(string host)
        => Assert.True(ControlApiGuard.CheckHost(host, Port).Allowed);

    [Theory]
    [InlineData("rebind.invalid:7879")]      // the rebinding name, on our own port
    [InlineData("rebind.invalid")]           // ...and without one
    [InlineData("attacker.example.com:7879")]
    [InlineData("127.0.0.1:7880")]           // right machine, another Director's port
    [InlineData("localhost:80")]
    [InlineData("192.168.1.10:7879")]        // this machine by its LAN address
    [InlineData("127.0.0.1")]                // implies port 80, which we did not bind
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? host)
        => Assert.False(ControlApiGuard.CheckHost(host, Port).Allowed);

    /// <summary>
    /// A refusal always says why. A gate that refuses silently turns a client's configuration
    /// mistake into an unexplained failure, and this one sits in front of every route.
    /// </summary>
    [Fact]
    public void A_refused_host_is_named_in_the_reason()
    {
        var verdict = ControlApiGuard.CheckHost("rebind.invalid", Port);

        Assert.False(verdict.Allowed);
        Assert.Contains("rebind.invalid", verdict.Reason, StringComparison.Ordinal);
    }

    // ---------- The cross-site mutation gate ----------

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void Reads_are_not_subject_to_the_cross_site_gate(string method)
        => Assert.True(ControlApiGuard.CheckCrossSiteMutation(method, "cross-site", "https://attacker.invalid", Port).Allowed);

    /// <summary>A non-browser client sends neither header and is allowed; only browsers add them,
    /// and a page cannot remove them from its own requests.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void A_non_browser_mutation_is_allowed(string method)
        => Assert.True(ControlApiGuard.CheckCrossSiteMutation(method, null, null, Port).Allowed);

    [Fact]
    public void A_same_origin_browser_mutation_is_allowed()
        => Assert.True(ControlApiGuard.CheckCrossSiteMutation("POST", "same-origin", $"http://127.0.0.1:{Port}", Port).Allowed);

    [Theory]
    [InlineData("cross-site")]
    [InlineData("cross-origin")]
    // "same-site" is the one that would slip past a gate written to refuse the literal string
    // "cross-site": a browser calls another PORT on this machine same-site, because a port does not
    // start a new site. Any other local daemon serving a page would then be able to drive us.
    [InlineData("same-site")]
    [InlineData("none")]
    public void A_browser_mutation_that_is_not_same_origin_is_refused(string secFetchSite)
        => Assert.False(ControlApiGuard.CheckCrossSiteMutation("POST", secFetchSite, null, Port).Allowed);

    [Theory]
    [InlineData("https://attacker.invalid")]
    [InlineData("http://localhost:7880")]
    [InlineData("http://127.0.0.1:7880")]
    [InlineData("null")]
    public void A_mutation_with_a_foreign_origin_is_refused(string origin)
        => Assert.False(ControlApiGuard.CheckCrossSiteMutation("POST", null, origin, Port).Allowed);

    // ---------- What a session-child credential may reach ----------

    private static readonly Guid Own = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static bool ChildMay(string method, string path, string? sessionIdQuery = null)
        => ControlApiGuard.CheckSessionChild(method, path, _ => sessionIdQuery, Own).Allowed;

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/fleet/sessions")]
    [InlineData("/fleet/repositories")]
    [InlineData("/fleet/worktrees")]
    public void A_child_may_read_the_safe_discovery_set(string path)
        => Assert.True(ChildMay("GET", path));

    [Theory]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/fleet-preamble")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/fleet-preamble-hook-output")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/claude-hook")]
    public void A_child_may_reach_its_own_session(string method, string path)
        => Assert.True(ChildMay(method, path));

    [Theory]
    [InlineData("GET", "/sessions/22222222-2222-2222-2222-222222222222/fleet-preamble")]
    [InlineData("GET", "/sessions/22222222-2222-2222-2222-222222222222/fleet-preamble-hook-output")]
    [InlineData("POST", "/sessions/22222222-2222-2222-2222-222222222222/claude-hook")]
    public void A_child_may_not_reach_another_session(string method, string path)
        => Assert.False(ChildMay(method, path));

    [Fact]
    public void A_child_may_read_its_own_terminal_buffer()
        => Assert.True(ChildMay("GET", "/fleet/buffer", Own.ToString()));

    [Theory]
    [InlineData("22222222-2222-2222-2222-222222222222")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void A_child_may_not_read_another_terminal_buffer(string sessionIdQuery)
        => Assert.False(ChildMay("GET", "/fleet/buffer", sessionIdQuery));

    /// <summary>
    /// The dangerous set, refused to a child by DEFAULT rather than by enumeration. The list here is
    /// a sample - the rule is an allow list, so a route added to the product tomorrow is refused to a
    /// child until somebody deliberately grants it, which is the opposite of a deny list where every
    /// new route is open until someone remembers.
    /// </summary>
    [Theory]
    [InlineData("POST", "/shutdown")]
    [InlineData("POST", "/reconnect")]
    [InlineData("POST", "/fleet/spawn")]
    [InlineData("POST", "/fleet/prompt")]
    [InlineData("POST", "/fleet/send")]
    [InlineData("POST", "/fleet/broadcast")]
    [InlineData("POST", "/fleet/interrupt")]
    [InlineData("GET", "/settings")]
    [InlineData("PUT", "/settings")]
    [InlineData("POST", "/settings/agents")]
    [InlineData("POST", "/tools/run")]
    [InlineData("GET", "/browsers")]
    [InlineData("POST", "/browsers/abc/start")]
    [InlineData("GET", "/fleet/machines")]
    [InlineData("GET", "/history")]
    [InlineData("GET", "/some/route/invented/tomorrow")]
    public void A_child_may_not_reach_anything_else(string method, string path)
        => Assert.False(ChildMay(method, path));

    /// <summary>
    /// The own-session routes are matched by NAME, not by prefix. Without this a child could reach
    /// any future /sessions/{own-id}/... route the moment it was added, simply because the id in the
    /// path happened to be its own.
    /// </summary>
    [Theory]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/history")]
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/anything-new")]
    public void An_own_session_path_is_not_a_blanket_grant(string method, string path)
        => Assert.False(ChildMay(method, path));

    [Fact]
    public void A_trailing_slash_does_not_change_the_answer()
    {
        Assert.True(ChildMay("GET", "/fleet/sessions/"));
        Assert.False(ChildMay("PUT", "/settings/"));
    }
}
