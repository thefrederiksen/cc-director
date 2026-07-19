using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1869: on hosted you could SEE sessions and do NOTHING with them.
///
/// The read path was made tenant-aware and resolves the request's tenant from the authenticated device key.
/// The COMMAND path was not: twenty-three per-session routes passed a literal <c>TenantId.Local</c> into the
/// session locator. On hosted, where a request's tenant is a real account, that read the empty Local
/// partition, so prompt, interrupt, escape, buffer, summary, git, wingman, role, hold and delete all answered
/// 404 "session not found across any director" for a correctly enrolled Director whose sessions the roster
/// was listing perfectly. Because /buffer was among them the terminal view was dead too.
///
/// It was INVISIBLE on self-host, because there the request's tenant genuinely IS Local, so every existing
/// test agreed with the bug. Only a real Director driven against the real hosted box found it. These tests
/// are the hosted case that was missing.
///
/// WHAT IS ASSERTED, and why it is asserted this way. Every route is checked in BOTH directions:
///   - the owner's own key must NOT get the locator's not-found body - the session was found;
///   - the OTHER tenant's key on the same session MUST get exactly that 404 - isolation still holds.
/// The second half is what stops this being a test that would pass if the fix had simply made every route
/// locate every session regardless of tenant. Matching the locator's exact error body, rather than a bare
/// status code, is deliberate: these routes can legitimately answer non-200 for reasons that have nothing to
/// do with tenancy (a verb the fake Director does not implement, a session in the wrong state), and a
/// status-code assertion would confuse those with the defect under test.
///
/// Revert-prove: put a literal TenantId.Local back into any one route's locate call - or change
/// LocateSessionForRequestAsync to ignore the request and pass TenantId.Local - and that route's owner-side
/// assertion goes RED with the not-found body, while its cross-tenant assertion stays green.
///
/// This drives a REAL GatewayHost over REAL HTTP through the REAL auth middleware, with two REAL tunnel
/// Directors on two different tenants - the same wire path a production Director uses.
/// </summary>
public sealed class HostedSessionCommandRouteTenancyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "sess-a";
    private const string SessB = "sess-b";

    /// <summary>The exact body the session locator produces when it finds nothing. This IS the defect's signature.</summary>
    private const string NotLocated = "session not found across any director";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;
    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cmd-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", "tenant-bob");

        // Both fake Directors answer every verb, so a route that reaches its Director gets a real answer and
        // any not-found body can only have come from the locate step - the step under test.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA", dispatch: AnswerAnything);
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB", dispatch: AnswerAnything);
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    private static DirectorCommandResult AnswerAnything(DirectorCommand cmd) =>
        FakeTunnelDirector.Ok(new { ok = true, lines = Array.Empty<string>(), items = Array.Empty<object>() });

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The per-session routes the live hosted run reported dead, as (method, path-after-the-session-id, body).
    /// /buffer leads because it is the terminal view: if that one is dead the feature is dead whatever else works.
    /// </summary>
    public static IEnumerable<object[]> SessionRoutes() => new List<object[]>
    {
        new object[] { "GET",    "/buffer",         null! },
        new object[] { "GET",    "/summary",        null! },
        new object[] { "GET",    "/git",            null! },
        new object[] { "GET",    "/wingman",        null! },
        new object[] { "GET",    "/handover",       null! },
        new object[] { "GET",    "/recap",          null! },
        new object[] { "POST",   "/interrupt",      "{}" },
        new object[] { "POST",   "/escape",         "{}" },
        new object[] { "POST",   "/prompt",         "{\"text\":\"hello\"}" },
        new object[] { "POST",   "/role",           "{\"role\":\"Worker\"}" },
        new object[] { "POST",   "/hold",           "{\"onHold\":true}" },
        new object[] { "POST",   "/transcribing",   "{\"transcribing\":true}" },
        new object[] { "POST",   "/wingman/ask",    "{\"question\":\"why\"}" },
        new object[] { "POST",   "/wingman/goal",   "{\"goal\":\"ship\"}" },
        new object[] { "POST",   "/recap",          "{}" },
        new object[] { "POST",   "/request-deletion", "{\"reason\":\"done\"}" },
        new object[] { "DELETE", "",                null! },
    };

    [Theory]
    [MemberData(nameof(SessionRoutes))]
    public async Task The_owning_tenant_can_reach_its_own_session(string method, string suffix, string? body)
    {
        // The defect, directly: on hosted this used to be the locator's 404 for EVERY one of these, because the
        // route asked the Local partition while the caller's tenant was a real account.
        var resp = await Send(method, $"sessions/{SessA}{suffix}", _keyA, body);
        var text = await resp.Content.ReadAsStringAsync();

        Assert.False(text.Contains(NotLocated, StringComparison.Ordinal),
            $"{method} /sessions/{{sid}}{suffix} could not locate its OWN tenant's session - " +
            $"status {(int)resp.StatusCode}, body: {text}");
    }

    [Theory]
    [MemberData(nameof(SessionRoutes))]
    public async Task Another_tenant_cannot_reach_it(string method, string suffix, string? body)
    {
        // The other direction, and the reason the test above is not satisfied by "locate everything". Tenant B
        // naming tenant A's session must still get exactly the locator's not-found answer.
        var resp = await Send(method, $"sessions/{SessA}{suffix}", _keyB, body);
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains(NotLocated, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_and_the_commands_agree()
    {
        // The shape of the bug in one assertion: the roster listing a session while the commands cannot find
        // it is precisely "you can see it and do nothing with it". They must now answer about the same world.
        var roster = await (await Send("GET", "sessions", _keyA, null)).Content.ReadAsStringAsync();
        Assert.Contains(SessA, roster, StringComparison.Ordinal);

        var buffer = await (await Send("GET", $"sessions/{SessA}/buffer", _keyA, null)).Content.ReadAsStringAsync();
        Assert.False(buffer.Contains(NotLocated, StringComparison.Ordinal));
    }

    private Task<HttpResponseMessage> Send(string method, string path, string deviceKey, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Idle",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
