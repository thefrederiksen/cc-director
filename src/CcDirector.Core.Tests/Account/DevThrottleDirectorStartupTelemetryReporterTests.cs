using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director-startup telemetry reporter sends the Gateway contract (Gateway Centralization
/// Phase 1, issue #632): a POST to <c>&lt;gateway.url&gt;/telemetry/director-startup</c> with a body carrying
/// director_id, machine_name and the optional app_version; that a non-success response surfaces as a
/// thrown error the best-effort caller logs; and that with no Gateway configured the reporter is a
/// logged no-op that makes no direct call to the cloud.
///
/// Every test passes an explicit <c>gatewayUrl</c> so the reporter never reads the test machine's
/// config.json - the target is determined by the test, not the environment.
/// </summary>
public sealed class DevThrottleDirectorStartupTelemetryReporterTests
{
    private const string TestGatewayUrl = "http://127.0.0.1:7878";

    [Fact]
    public async Task ReportStartupAsync_PostsToGatewayWithDirectorIdMachineAndVersion()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal($"{TestGatewayUrl}{DevThrottleDirectorStartupTelemetryReporter.GatewayStartupPath}", handler.Request.RequestUri!.ToString());

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal("dir-abc", (string?)body["director_id"]);
        Assert.Equal("TEST-MACHINE", (string?)body["machine_name"]);
        Assert.Equal("1.2.3", (string?)body["app_version"]);
    }

    [Fact]
    public async Task ReportStartupAsync_SendsNoAuthorizationHeader_WhenNoGatewayTokenIsConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        // This test used to assert the POST carries NO Authorization header EVER, on the #642 reasoning that
        // "the Director holds no credential, and the Gateway attaches its own token on the cloud forward
        // (#639)". The second half is still true and is enforced Gateway-side; the first half conflated two
        // different hops. The Director does hold a credential for the GATEWAY - the per-device key enrollment
        // writes to gateway.token - and the inbound hop is gated host-wide like every other Gateway route, so
        // sending nothing meant a 401 that the best-effort caller swallowed (issue #1855).
        //
        // What survives is this: with NO gateway token configured there is nothing to send, and the reporter
        // must not invent one. The credential-carrying case is proven below.
        Assert.NotNull(handler.Request);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task ReportStartupAsync_SendsTheConfiguredGatewayTokenAsBearer()
    {
        // Issue #1855: the fix. A correctly enrolled Director was refused 401 because this report was the one
        // Director-to-Gateway call that carried no credential, and the failure was swallowed - so the only
        // symptom was startup telemetry that never arrived, which is indistinguishable from nobody starting a
        // Director. It now sends the same gateway.token Bearer every other call sends.
        //
        // Revert-prove, run verbatim against the final file: delete the two lines
        //     if (authenticated)
        //         request.Headers.Authorization = new ...AuthenticationHeaderValue("Bearer", _gatewayToken);
        // from ReportStartupAsync. The build succeeds, and EXACTLY TWO tests redden on a null Authorization
        // header - this one and SendsTheCredentialWhenTheOverridePointsAtItsOwnGateway, which is the other
        // test asserting the header is sent. The remaining nine stay green, including the cross-host test,
        // which asserts the header is ABSENT and therefore cannot detect this revert at all. That is exactly
        // why the own-gateway control exists: without it, deleting the assignment would leave the cross-host
        // guard green and the deletion would look harmless.
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl, gatewayToken: "device-key-1855");

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Request);
        Assert.NotNull(handler.Request.Headers.Authorization);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("device-key-1855", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ReportStartupAsync_DoesNotFollowARedirect_SoTheCredentialCannotBeBouncedOffHost()
    {
        // Issue #1855, the redirect hole - closed structurally rather than left to framework behaviour.
        //
        // The Bearer is attached only when the target host matches the configured Gateway, but a redirect
        // moves the destination AFTER that check has passed: a request that legitimately starts at this
        // Director's own Gateway could be bounced anywhere, carrying a key that authenticates every
        // Director-to-Gateway call. So the reporter's own client has AllowAutoRedirect=false.
        //
        // This drives the REAL handler the reporter ships with (CreateDefaultHandler), over REAL sockets,
        // against a server that answers 302 to a DIFFERENT origin - it does not assert a configuration flag and
        // call that a proof. The second server is watched: it must never be contacted at all.
        //
        // Deliberately NOT resting on ".NET strips Authorization across origins". That is true today and it is
        // framework internals; this guard has to be visible in our own file and provable here.
        //
        // Revert-prove, run verbatim against the final file: set AllowAutoRedirect = true in
        // CreateDefaultHandler. The build succeeds and EXACTLY THIS ONE test reddens, on
        // Assert.Equal(0, destination.Requests) with Expected 0 / Actual 1 - the redirect target really was
        // contacted - while the other eleven stay green.
        using var destination = new LoopbackServer(_ => (200, null));
        using var redirector = new LoopbackServer(_ => (302, destination.Url));

        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(DevThrottleDirectorStartupTelemetryReporter.CreateDefaultHandler()),
            machineName: "TEST-MACHINE", gatewayUrl: redirector.Url.TrimEnd('/'), gatewayToken: "device-key-1855");

        // The exception is RECORDED rather than required, so the SECURITY fact is the assertion under proof
        // and reddens first. Requiring the throw up front would make this test fail on "the redirect was
        // followed at all" and never reach the question that matters, which is whether the credential moved.
        var thrown = await Record.ExceptionAsync(() => reporter.ReportStartupAsync("dir-abc"));

        Assert.Equal(0, destination.Requests);       // the redirect target was never contacted
        Assert.Null(destination.LastAuthorization);  // so the credential could not have reached it
        Assert.Equal(1, redirector.Requests);

        // Positive control: the redirecting server IS the configured Gateway, so it legitimately received the
        // credential. Without this, "the destination saw no Authorization" would also hold if the reporter had
        // simply never sent one - which is the failure mode this whole pull request is about.
        Assert.Equal("Bearer device-key-1855", redirector.LastAuthorization);

        // And the unfollowed 302 stays a non-success response, so it still surfaces as the throw the
        // best-effort caller logs rather than being mistaken for a delivered report.
        Assert.IsType<HttpRequestException>(thrown);
    }

    [Fact]
    public async Task ReportStartupAsync_NeverSendsTheGatewayCredentialToAnotherHost()
    {
        // The credential rides ONLY to this Director's own configured Gateway. DEVTHROTTLE_STARTUP_TELEMETRY_URL
        // can point the report at an arbitrary address for a test, proof or staging run, and attaching the key
        // unconditionally would hand this machine's per-device Gateway key - which authenticates every
        // Director-to-Gateway call - to whatever host that variable names, by setting one environment variable.
        var previous = Environment.GetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar);
        Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, "http://evil.example.com/collect");
        try
        {
            var handler = new CapturingHandler(HttpStatusCode.OK);
            var reporter = new DevThrottleDirectorStartupTelemetryReporter(
                new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl, gatewayToken: "device-key-1855");

            await reporter.ReportStartupAsync("dir-abc");

            Assert.NotNull(handler.Request);
            Assert.Equal("http://evil.example.com/collect", handler.Request.RequestUri!.ToString());
            Assert.Null(handler.Request.Headers.Authorization);           // the key did not travel
            Assert.DoesNotContain("device-key-1855", handler.Body ?? ""); // and is not in the body either
        }
        finally
        {
            Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, previous);
        }
    }

    [Fact]
    public async Task ReportStartupAsync_SendsTheCredentialWhenTheOverridePointsAtItsOwnGateway()
    {
        // Control for the test above, and it is the one that stops that guard being a blanket "never
        // authenticate when the override is set". An override naming this Director's OWN Gateway is still its
        // own Gateway, so the credential travels and the report is accepted. Without this control, deleting the
        // Authorization assignment entirely would leave the cross-host test green.
        var previous = Environment.GetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar);
        Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, $"{TestGatewayUrl}/telemetry/director-startup");
        try
        {
            var handler = new CapturingHandler(HttpStatusCode.OK);
            var reporter = new DevThrottleDirectorStartupTelemetryReporter(
                new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl, gatewayToken: "device-key-1855");

            await reporter.ReportStartupAsync("dir-abc");

            Assert.NotNull(handler.Request);
            Assert.Equal("device-key-1855", handler.Request.Headers.Authorization?.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, previous);
        }
    }

    [Fact]
    public async Task ReportStartupAsync_TrailingSlashGatewayUrl_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl + "/");

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Request);
        Assert.Equal($"{TestGatewayUrl}{DevThrottleDirectorStartupTelemetryReporter.GatewayStartupPath}", handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReportStartupAsync_OmitsAppVersionWhenBlank()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal("dir-abc", (string?)body["director_id"]);
        Assert.Equal("TEST-MACHINE", (string?)body["machine_name"]);
        Assert.False(body.ContainsKey("app_version"));
    }

    [Fact]
    public async Task ReportStartupAsync_NoGatewayConfigured_IsNoOpAndMakesNoCall()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: "");

        // Must not throw, and must make no HTTP call (no direct cloud call).
        await reporter.ReportStartupAsync("dir-abc");

        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ReportStartupAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportStartupAsync("dir-abc"));
    }

    [Fact]
    public async Task ReportStartupAsync_EmptyDirectorId_Throws()
    {
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK)), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<ArgumentException>(() => reporter.ReportStartupAsync(""));
    }

    [Fact]
    public async Task ReportStartupAsync_NullMachineName_DefaultsToEnvironmentMachineName()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: null, gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal(Environment.MachineName, (string?)body["machine_name"]);
    }

    /// <summary>Captures the outgoing request and returns a configured status, so no real network call is made.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status) => _status = status;

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }

    /// <summary>
    /// A real loopback HTTP server, so the redirect guard is proven over REAL sockets with the reporter's REAL
    /// handler rather than by asserting a configuration flag. Records how many requests it received and the
    /// Authorization header of the last one, which is what lets the test say the credential did not travel.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Func<System.Net.HttpListenerRequest, (int Status, string? Location)> _respond;
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }
        public int Requests { get; private set; }
        public string? LastAuthorization { get; private set; }

        public LoopbackServer(Func<System.Net.HttpListenerRequest, (int Status, string? Location)> respond)
        {
            _respond = respond;
            Url = $"http://localhost:{FreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; /* listener stopped */ }

                Requests++;
                LastAuthorization = ctx.Request.Headers["Authorization"];
                var (status, location) = _respond(ctx.Request);
                ctx.Response.StatusCode = status;
                if (location is not null)
                    ctx.Response.Headers["Location"] = location;
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
            finally { l.Stop(); }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }
    }
}
