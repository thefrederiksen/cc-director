using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1855: the Gateway half of the director-startup telemetry 401.
///
/// The reported symptom was that a correctly enrolled hosted Director could not report its startup: the
/// route answered 401 to the very per-device key the hosted Gateway had just issued it, and the report is
/// best-effort with the exception swallowed, so nothing surfaced anywhere. The only symptom was an ABSENCE -
/// hosted Directors simply never appeared in startup telemetry - and an absence looks exactly like nobody
/// having started a Director.
///
/// The cause turned out NOT to be hosted rejecting the key. The reporter sent NO Authorization header at all
/// (fixed in DevThrottleDirectorStartupTelemetryReporter), so the host-wide gate refused it - which would
/// happen on ANY Gateway with auth on, self-host included. Hosted merely surfaced it first, because hosted is
/// authenticated by construction.
///
/// These tests pin the Gateway side of that conclusion, which the client-side fix depends on and which no
/// test previously covered: this route accepts a per-device key exactly like every other gated route, so
/// sending the credential is sufficient. If that ever stopped being true the client fix would silently stop
/// working and the failure would again be invisible.
///
/// WHAT THEY DELIBERATELY DO NOT PROVE: anything about tenancy. This endpoint does not read the request's
/// tenant - it writes a process-global record line and, when forwarding is configured, enqueues the raw body
/// globally with no tenant on it. So the credential here is an ADMISSION check, not an attribution: any valid
/// key gets in, including one bound to no tenant, and that is the correct behaviour for telemetry that is
/// global by construction. These tests assert exactly that and no more. Per-account attribution would need a
/// tenant-aware record and queue contract, which is a separate piece of work.
/// </summary>
public sealed class DirectorStartupTelemetryAuthTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Path_ = "telemetry/director-startup";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _deviceKey = "";

    private readonly string _instancesDir =
        Path.Combine(System.IO.Path.GetTempPath(), "cc-dst-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _deviceKey = _gateway.Devices.Register("dev-telemetry", "MT").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_per_device_key_is_accepted()
    {
        // The claim the client fix rests on: this route takes a per-device key like every other gated route,
        // so a Director that simply SENDS its gateway.token is accepted. 202 is the endpoint's success answer
        // ("received and recorded"), not 200.
        var resp = await Post(_deviceKey);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task The_shared_gateway_token_is_also_accepted()
    {
        // Control: the device-key branch is not the only way in, so a self-host Director configured with the
        // shared machine token reports its startup too. Without this, a route that ONLY took device keys would
        // satisfy the test above while breaking every self-host install.
        var resp = await Post(Token);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task No_credential_is_refused()
    {
        // The reported failure, reproduced at the wire: this is EXACTLY what the reporter used to send - a
        // POST with no Authorization header at all - and it is exactly what it got back. The route is gated,
        // as it should be; the client was the one at fault.
        var req = new HttpRequestMessage(HttpMethod.Post, Path_)
        {
            Content = new StringContent("{\"director_id\":\"dir-1\"}", Encoding.UTF8, "application/json"),
        };
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_bogus_key_is_refused()
    {
        // Control on the acceptance tests above: they must pass because the CREDENTIAL was valid, not because
        // the route waves everything through. A route that accepted anything would pass both of them.
        var resp = await Post("not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private Task<HttpResponseMessage> Post(string credential)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Path_)
        {
            Content = new StringContent("{\"director_id\":\"dir-1\",\"machine_name\":\"MT\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
