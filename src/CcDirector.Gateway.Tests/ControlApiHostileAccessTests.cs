using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hostile tests for the Director's local trust boundary.
///
/// Every test here drives a REAL <see cref="ControlApiHost"/> over real HTTP, and every one of them
/// was watched FAILING against the code as it stood before this work - the commit that added this
/// file changed no product behaviour, precisely so these could be seen red first. A check that has
/// never been seen failing is decoration.
///
/// The host is constructed with NO <c>authEnabled</c> argument on purpose. "Authentication is
/// required by DEFAULT" is the property under test; a test that switched it on itself would prove
/// only that the switch exists, which was never in doubt - it existed before this mission and
/// production did not use it.
///
/// What these do NOT prove is stated plainly rather than left for a reader to assume: a process
/// running as the desktop user can still read the machine secret off disk and mint itself full
/// authority. The session-child credential is least privilege, not an operating-system sandbox.
/// Closing that needs a transport where the operating system asserts the caller's identity, which is
/// a separate piece of work.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostileAccessTests : IAsyncLifetime
{
    private readonly string _instancesDir;
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private int _port;

    /// <summary>The base address every client in this class talks to.</summary>
    private Uri BaseAddress => new($"http://127.0.0.1:{_port}/");

    public ControlApiHostileAccessTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-hostile-instances-" + unique);

        // Isolate the machine-global storage root. With auth on, the host resolves its accepted secret
        // from the gateway token when this machine has one; a fleet machine's real config.json would
        // therefore make the host accept a secret this test never sees, and every assertion below would
        // pass for the wrong reason. A fresh temp root gives an empty config, so the root secret is the
        // local token file and this test and the host genuinely share one secret.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hostile-root-" + unique);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        // No authEnabled argument: the production default is what is on trial.
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, instancesDirectory: _instancesDir);
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

    private string RootSecret => DirectorAuth.ResolveAcceptedToken(GatewayConfig.Load().Token);

    private HttpClient Anonymous() => new() { BaseAddress = BaseAddress };

    private HttpClient WithToken(string token)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // =====================================================================================
    // 1. No token -> 401 on every route except /healthz
    // =====================================================================================

    /// <summary>
    /// One route from every mapper on the surface. GET /settings leads deliberately: it returns the
    /// whole of config.json including the fleet token, so whoever can read it unauthenticated owns
    /// the fleet - it is the single highest-value read on the Director.
    /// </summary>
    public static TheoryData<string, string> UnauthenticatedRoutes() => new()
    {
        { "GET", "settings" },
        { "GET", "settings/agents" },
        { "GET", "fleet/sessions" },
        { "GET", "fleet/repositories" },
        { "GET", "fleet/worktrees" },
        { "GET", "fleet/machines" },
        { "GET", "prompt-delivery-failures" },
        { "GET", "update/status" },
        { "GET", "tools" },
        { "GET", "browsers" },
        { "GET", "workspaces" },
        { "GET", "history" },
        { "POST", "shutdown" },
        { "POST", "reconnect" },
        { "POST", "fleet/spawn" },
        { "POST", "fleet/prompt" },
        { "POST", "fleet/send" },
        { "POST", "fleet/broadcast" },
        { "POST", "fleet/rename" },
        { "POST", "tools/run" },
        { "POST", "settings/test/gateway" },
        { "PUT", "settings" },
    };

    [Theory]
    [MemberData(nameof(UnauthenticatedRoutes))]
    public async Task NoToken_EveryRoute_Is401(string method, string path)
    {
        using var anonymous = Anonymous();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_Healthz_Is200()
    {
        using var anonymous = Anonymous();
        using var response = await anonymous.GetAsync("healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Public and MINIMAL are two separate properties, and only the first one was ever true. An
    /// unauthenticated /healthz answered with this Director's identifier, the machine's name, the
    /// product version and a live session count - configuration handed to anyone who asked, on the
    /// one route that by design asks for nothing.
    /// </summary>
    [Fact]
    public async Task NoToken_Healthz_SaysLivenessAndNothingElse()
    {
        using var anonymous = Anonymous();
        var body = await anonymous.GetStringAsync("healthz");

        Assert.Contains("\"status\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_host.DirectorId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessions", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machineName", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of trimming /healthz, and the one that would have broken production quietly.
    /// The Director's own startup self-probe calls /healthz over real HTTP and looks for its OWN
    /// identifier in the answer - that is how it proves no other service is shadowing the port it
    /// just bound. If the authenticated answer stopped naming it, the probe would fail on a
    /// perfectly healthy Director, log SELF-PROBE FAILED, and release its port reservation. Same for
    /// the launcher's update check, which reads the version and the session count from here to decide
    /// whether a swap would interrupt live work.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedHealthz_StillNamesTheDirectorAndItsVersion()
    {
        using var client = WithToken(DirectorScopedToken.Mint(RootSecret, ScopeNames.Admin));
        var body = await client.GetStringAsync("healthz");

        Assert.Contains(_host.DirectorId, body, StringComparison.Ordinal);
        Assert.Contains("1.0.0-test", body, StringComparison.Ordinal);
        Assert.Contains("\"sessions\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // 2. A hostile Host header is refused - the DNS-rebinding defence
    // =====================================================================================

    /// <summary>
    /// The shape of a real rebinding attack: the victim's browser has been told that some name the
    /// attacker controls resolves to 127.0.0.1, so the connection genuinely arrives on loopback and
    /// the peer genuinely is this machine - and the ONLY thing that distinguishes it from the
    /// Director's own clients is the name in the Host header. A live probe before this work returned
    /// HTTP 200 to Host: rebind.invalid.
    /// </summary>
    [Theory]
    [InlineData("rebind.invalid")]
    [InlineData("attacker.example.com")]
    [InlineData("cc-director.local")]
    public async Task HostileHost_OnARead_IsRefused(string hostileHost)
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "settings");
        request.Headers.Host = hostileHost;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HostileHost_OnAMutation_IsRefused()
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Put, "settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = "rebind.invalid";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A Host naming the right machine but the WRONG port. This is what a page served by some other
    /// local daemon looks like, and it is refused for the same reason: the allowlist is the exact
    /// authority this Director bound, not a family of addresses that happen to be loopback.
    /// </summary>
    [Fact]
    public async Task HostWithForeignPort_IsRefused()
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "settings");
        request.Headers.Host = $"127.0.0.1:{_port + 1}";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task TheDirectorsOwnLoopbackAuthority_IsAccepted(string name)
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "healthz");
        request.Headers.Host = $"{name}:{_port}";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // =====================================================================================
    // 3 and 4. Cross-site mutations are refused, including a plain form POST
    // =====================================================================================

    /// <summary>
    /// What a page on an attacker's site can actually do to a loopback service: it cannot read the
    /// answer, but without a server-side gate the mutation still HAPPENS. A full-authority token is
    /// attached here deliberately - the point is that the request is refused on the cross-site
    /// evidence alone, so a credential that leaked into a browser does not buy the attacker the
    /// route.
    /// </summary>
    [Fact]
    public async Task CrossSiteMutation_WithHostileOrigin_IsRefused()
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Put, "settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://attacker.invalid");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The simple form POST. It matters on its own because it is the request a browser will send
    /// with no permission from anybody: no preflight, no scripting, nothing for a CORS policy to
    /// refuse - just an HTML form on any page in the world, submitted to loopback, with the
    /// content type a form is allowed to use.
    /// </summary>
    [Fact]
    public async Task SimpleFormPost_FromAnotherSite_IsRefused()
    {
        using var anonymous = Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, "fleet/spawn")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["repoPath"] = @"C:\" }),
        };
        request.Headers.Add("Origin", "https://attacker.invalid");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Sec-Fetch-Mode", "no-cors");

        using var response = await anonymous.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"a cross-site form POST to /fleet/spawn was answered {(int)response.StatusCode}");
    }

    /// <summary>
    /// A page on ANOTHER port of this same machine. A browser calls that "same-site", not
    /// "same-origin", because a port does not start a new site - so a gate that only refused the
    /// literal string "cross-site" would wave this through, and any other local daemon serving a
    /// page would be able to drive the Director.
    /// </summary>
    [Fact]
    public async Task MutationFromAnotherLocalPort_IsRefused()
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Put, "settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", $"http://127.0.0.1:{_port + 1}");
        request.Headers.Add("Sec-Fetch-Site", "same-site");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The rebinding shape aimed at a MUTATION rather than a read, which is the version that costs
    /// something: the attacker cannot read our answer across origins, but a spawn or a shutdown does
    /// not need to be read to have happened.
    /// </summary>
    [Fact]
    public async Task DnsRebindingStyleHost_OnShutdown_IsRefused()
    {
        using var anonymous = Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, "shutdown");
        request.Headers.Host = "rebind.invalid";

        using var response = await anonymous.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"POST /shutdown with Host: rebind.invalid was answered {(int)response.StatusCode}");
    }

    // =====================================================================================
    // 5. A session-child credential is bound to its session and cannot reach the dangerous set
    // =====================================================================================

    private static readonly Guid SessionA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private string ChildTokenFor(Guid sessionId)
        => DirectorScopedToken.Mint(RootSecret, ScopeNames.SessionChild, sessionId);

    [Theory]
    [InlineData("POST", "shutdown")]
    [InlineData("POST", "reconnect")]
    [InlineData("POST", "fleet/spawn")]
    [InlineData("POST", "fleet/prompt")]
    [InlineData("POST", "fleet/send")]
    [InlineData("POST", "fleet/broadcast")]
    [InlineData("POST", "fleet/rename")]
    [InlineData("POST", "fleet/interrupt")]
    [InlineData("POST", "tools/run")]
    [InlineData("PUT", "settings")]
    [InlineData("POST", "settings/agents")]
    [InlineData("POST", "browsers")]
    public async Task SessionChild_CannotReachTheDangerousSet(string method, string path)
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using var response = await child.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The single highest-value read on the Director. A child that could call it would hold the
    /// fleet token a moment later, and every other restriction on it would be theatre.
    /// </summary>
    [Fact]
    public async Task SessionChild_CannotReadSettings()
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.GetAsync("settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("sessions/{other}/fleet-preamble")]
    [InlineData("sessions/{other}/fleet-preamble-hook-output")]
    public async Task SessionChild_CannotReadAnotherSession(string template)
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.GetAsync(template.Replace("{other}", SessionB.ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SessionChild_CannotReportAnotherSessionsClaudeHook()
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.PostAsync($"sessions/{SessionB}/claude-hook",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The terminal scrollback is every keystroke and every screen the agent has seen, secrets
    /// included, so it is the one read where naming the wrong session id matters most.
    /// </summary>
    [Fact]
    public async Task SessionChild_CannotReadAnotherSessionsTerminalBuffer()
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.GetAsync($"fleet/buffer?sessionId={SessionB}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A child token whose scope has been edited from session-child to admin. The scope is part of
    /// the signed material, so this is not a token at all once it has been touched.
    /// </summary>
    [Fact]
    public async Task ATamperedChildToken_IsNotACredential()
    {
        var tampered = ChildTokenFor(SessionA).Replace(ScopeNames.SessionChild, ScopeNames.Admin);
        using var client = WithToken(tampered);

        using var response = await client.GetAsync("settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A child token re-pointed at another session id by string surgery. Same reason: the bound id
    /// is signed, so rewriting it invalidates the signature rather than moving the grant.
    /// </summary>
    [Fact]
    public async Task ARepointedChildToken_IsNotACredential()
    {
        var repointed = ChildTokenFor(SessionA).Replace(SessionA.ToString(), SessionB.ToString());
        using var client = WithToken(repointed);

        using var response = await client.GetAsync($"sessions/{SessionB}/fleet-preamble");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Positive controls: the child CAN do what it is for ----------

    /// <summary>
    /// The child's own-session reads must pass the gate. The assertion is that the request was not
    /// REFUSED - a session that does not exist on this test host answers 404 or an empty body, and
    /// that is fine: what is on trial here is the gate, and a 401 or 403 would mean the fleet
    /// preamble no longer reaches the agent it was written for.
    /// </summary>
    [Theory]
    [InlineData("fleet-preamble")]
    [InlineData("fleet-preamble-hook-output")]
    public async Task SessionChild_CanReadItsOwnSession(string route)
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.GetAsync($"sessions/{SessionA}/{route}");

        Assert.False(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"the child's own {route} was refused with {(int)response.StatusCode}");
    }

    [Fact]
    public async Task SessionChild_CanReportItsOwnClaudeHook()
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.PostAsync($"sessions/{SessionA}/claude-hook",
            new StringContent("{\"claudeSessionId\":\"x\"}", Encoding.UTF8, "application/json"));

        Assert.False(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"the child's own claude-hook was refused with {(int)response.StatusCode}");
    }

    /// <summary>Safe discovery: the roster is what a preamble needs to orient an agent in the fleet.</summary>
    [Theory]
    [InlineData("fleet/sessions")]
    [InlineData("fleet/repositories")]
    [InlineData("fleet/worktrees")]
    [InlineData("healthz")]
    public async Task SessionChild_CanReadTheSafeDiscoverySet(string path)
    {
        using var child = WithToken(ChildTokenFor(SessionA));
        using var response = await child.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // =====================================================================================
    // Positive controls: full authority still drives the product
    // =====================================================================================

    [Theory]
    [InlineData(ScopeNames.Admin)]
    [InlineData(ScopeNames.Cli)]
    public async Task AFullAuthorityToken_ReadsSettings(string scope)
    {
        using var client = WithToken(DirectorScopedToken.Mint(RootSecret, scope));
        using var response = await client.GetAsync("settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The raw machine secret keeps working. It IS the root, and cc-settings-api and the launcher
    /// present it today - breaking them to tidy the credential model would be a regression dressed
    /// as a fix.
    /// </summary>
    [Fact]
    public async Task TheRawMachineSecret_StillReadsSettings()
    {
        using var client = WithToken(RootSecret);
        using var response = await client.GetAsync("settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A mutating route in the dangerous set, driven end to end by the command line's credential:
    /// past the Host allowlist, past the cross-site gate, past the scope gate, into the handler, and
    /// the write lands. Without this the suite could go green by refusing everything.
    /// </summary>
    [Fact]
    public async Task TheCommandLineCredential_WritesSettings()
    {
        using var client = WithToken(DirectorScopedToken.Mint(RootSecret, ScopeNames.Cli));

        using var write = await client.PutAsJsonAsync("settings",
            new { hostileTestMarker = "written-by-the-cli-credential" });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        var readBack = await client.GetStringAsync("settings");
        Assert.Contains("written-by-the-cli-credential", readBack, StringComparison.Ordinal);
    }

    /// <summary>
    /// A same-origin mutation from the loopback origin itself is allowed. The cross-site gate has two
    /// failure directions and only one of them is visible in the hostile tests above; a gate that
    /// refused everything would satisfy all of them and break the product.
    /// </summary>
    [Fact]
    public async Task ASameOriginMutation_IsAllowed()
    {
        using var client = WithToken(RootSecret);
        using var request = new HttpRequestMessage(HttpMethod.Put, "settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", $"http://127.0.0.1:{_port}");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
