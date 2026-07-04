using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the mobile revoke round trip end to end at the Gateway (issue #908, slice 4): a phone that
/// enrolled with a local device key loses that key when the device is revoked on the account's Devices
/// page. Enrollment issues a LOCAL key that validates; after the cloud roster drops the device (a
/// website revoke) and the Gateway's periodic reconcile sweep runs, the same local key no longer
/// validates - so the next credentialed request 401s and the phone re-gates to Sign in. Runs against an
/// in-process STUB cloud (a real <see cref="DeviceRegistryClient"/> over one handler serving verify +
/// list + heartbeat, no network). The live website revoke is the owner's QA gate.
/// </summary>
public sealed class MobileEnrollRevokeRoundTripTests
{
    private const string CloudKey = "dtd_live_PHONE_KEY_908";
    private const string CloudId = "cloud-dev-908";
    private const string InstallId = "phone-install-908";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A stateful stub of the cloud device registry serving the three endpoints this round trip needs.
    /// Flipping <see cref="Revoked"/> models a website revoke: verify stops matching, the device leaves
    /// the GET /devices roster, and its heartbeat becomes 404 - the three signals the reconcile uses.
    /// </summary>
    private sealed class StubCloud : HttpMessageHandler
    {
        public bool Revoked { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (method == HttpMethod.Post && path == DeviceRegistryClient.VerifyPath)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var key = (string?)JsonNode.Parse(body)?["device_key"] ?? "";
                if (!Revoked && key == CloudKey)
                    return Json(HttpStatusCode.OK, $"{{\"data\":{{\"valid\":true,\"id\":\"{CloudId}\"}}}}");
                return Json(HttpStatusCode.OK, "{\"data\":{\"valid\":false}}");
            }

            if (method == HttpMethod.Get && path == DeviceRegistryClient.DevicesPath)
            {
                var row = Revoked
                    ? ""
                    : $"{{\"id\":\"{CloudId}\",\"name\":\"Pixel\",\"platform\":\"android\",\"device_type\":\"phone\",\"key_prefix\":\"dtd_live\",\"key_last4\":\"ab12\"}}";
                return Json(HttpStatusCode.OK, $"{{\"data\":[{row}]}}");
            }

            if (method == HttpMethod.Post && path == DeviceRegistryClient.HeartbeatPath)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var installId = (string?)JsonNode.Parse(body)?["install_id"] ?? "";
                if (!Revoked && installId == InstallId)
                    return Json(HttpStatusCode.OK, "{\"data\":{\"recorded\":true}}");
                return Json(HttpStatusCode.NotFound, "{\"error\":\"unknown install\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static DevThrottleAccountService MakeAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-revoke-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
            service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-908"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static DeviceRegistryClient ClientOver(StubCloud stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-revoke-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public async Task Enroll_then_website_revoke_drops_the_local_key()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        // Enroll: the phone hands over its cloud key and gets a local key that validates.
        var enroll = new MobileDeviceEnrollmentService(account, ClientOver(stub), devices);
        var outcome = await enroll.EnrollAsync(CloudKey, InstallId, "Pixel", "android");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        var localKey = outcome.LocalDeviceKey!;
        Assert.True(devices.IsValidDeviceKey(localKey), "the issued local key validates right after enrollment");

        // Revoke on the account's Devices page, then run the Gateway's periodic reconcile sweep.
        stub.Revoked = true;
        await new ChildDeviceMirrorService(account, ClientOver(stub), devices).ReconcileAsync();

        // The local key no longer validates: a website revoke has ended this phone's access. The next
        // /sessions call 401s, and the app clears the key and returns to Sign in (client.ts onUnauthorized).
        Assert.False(devices.IsValidDeviceKey(localKey), "after the account revoke + reconcile, the local key is dropped");
        Assert.Equal(0, devices.Count);
    }
}
