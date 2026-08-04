using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A Director computes a real, actionable explanation when a command fails. It used to be thrown away TWICE
/// before any human could read it, and fixing either leg alone lands nowhere:
///
///   LEG 1, the Gateway: <c>TunnelFailure</c>'s default branch collapsed every DIRECTOR-SENT failure
///   (BadRequest / NotFound / Conflict / Locked / Failed) into a BODYLESS 502. Its Gateway-SYNTHESIZED
///   branches - no tunnel, Timeout, TunnelDropped - already carried their words; the Director-sent default
///   never did.
///
///   LEG 2, the Director's own outbound client: <see cref="GatewayClient"/>'s failure helper threw
///   "returned HTTP 502 Bad Gateway" without ever reading the response body, so even a Gateway that DID send
///   the words had them discarded one hop before the person who typed the command. (The ~11 fleet relay
///   methods that first exposed this died with the Director's /fleet routes in the Remove-the-network-port
///   mission; the helper - RelayFailureAsync - survives under the remaining outbound calls, and leg 2 now
///   drives it through the desktop's snooze relay, RecordHoldAsync.)
///
/// These tests pin both legs against real transports, because that is the only way to know the message
/// actually arrives. Leg 1 rides a REAL SignalR tunnel to a REAL GatewayHost whose Director is registered at
/// a DELIBERATELY UNREACHABLE endpoint, so the answer can only have come over the tunnel. Leg 2 drives the
/// REAL GatewayClient over a REAL loopback HTTP connection.
///
/// Deliberately NOT pinned: any status change. Every status here stays exactly what it ships today.
/// </summary>
public sealed class DirectorErrorMessageReachesTheHumanTests
{
    // ===================== LEG 1: the Gateway carries the Director's words =====================

    [Collection("DirectorRoot")]
    public sealed class GatewayLeg : IAsyncLifetime
    {
        private const string Token = "test-token-director-error-words";
        private const string DirectorId = "dir-error-words";

        /// <summary>The Director's own explanation - the thing a human needs and used to never see.</summary>
        private const string DirectorWords = "that repository is already open in another session on this machine";

        private readonly string _root;
        private readonly string? _prevRoot;
        private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-errwords-" + Guid.NewGuid().ToString("N"));

        private GatewayHost _gateway = null!;
        private HttpClient _http = null!;
        private FakeTunnelDirector _director = null!;

        public GatewayLeg()
        {
            _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            _root = Path.Combine(Path.GetTempPath(), "ccd-errwords-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        }

        public async Task InitializeAsync()
        {
            _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
                instancesDirectory: _instancesDir,
                workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
                streamMode: true);
            await _gateway.StartAsync();
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            // Every verb fails the way a real Director fails: a DIRECTOR-SENT status carrying real words.
            // This is the branch that used to drop them.
            _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId,
                dispatch: _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, DirectorWords));
        }

        public async Task DisposeAsync()
        {
            await _director.DisposeAsync();
            _http.Dispose();
            await _gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
            foreach (var dir in new[] { _instancesDir, _root })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task DirectorSentFailure_carriesTheDirectorsWords_andKeepsTheSame502()
        {
            var resp = await _http.GetAsync($"directors/{DirectorId}/repos");

            // The status is UNCHANGED. A Director BadRequest ships as 502 on this leg today, and mapping it to
            // 400 would move a shipped contract - this change carries the words and nothing else.
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body), "a bodyless 502 is the bug: the human gets a bare status they cannot act on");

            using var doc = JsonDocument.Parse(body);
            Assert.Equal(DirectorWords, doc.RootElement.GetProperty("error").GetString());
        }

    }

    // ============ LEG 2: the Director's relay surfaces them instead of discarding them ============

    public sealed class DirectorRelayLeg
    {
        private const string GatewayWords = "session 4c81 is on hold; take it off hold before sending to it";

        /// <summary>A Gateway stub that fails exactly the way the fixed leg 1 above now fails.</summary>
        private static async Task<(WebApplication app, string url)> StartFailingGatewayAsync(
            int statusCode, string? jsonBody)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapPost("/sessions/{sid}/hold", () => jsonBody is null
                ? Results.StatusCode(statusCode)
                : Results.Content(jsonBody, "application/json", statusCode: statusCode));
            await app.StartAsync();
            return (app, app.Urls.First());
        }

        private static GatewayClient ClientFor(string url) =>
            new(new GatewayConfig { Url = url }, Guid.NewGuid().ToString(), "1.0.0");

        [Fact]
        public async Task Relay_surfacesTheGatewaysMessage_ratherThanABareStatusLine()
        {
            var (app, url) = await StartFailingGatewayAsync(502, $$"""{"error":"{{GatewayWords}}"}""");

            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ClientFor(url).RecordHoldAsync(Guid.NewGuid().ToString(), onHold: true));

                // The whole point: the human reads WHY, not "returned HTTP 502 Bad Gateway".
                Assert.Equal(GatewayWords, ex.Message);
                Assert.DoesNotContain("returned HTTP", ex.Message);
            }
            finally
            {
                await app.StopAsync();
            }
        }

        [Fact]
        public async Task Relay_withNoBody_stillReportsTheStatus_ratherThanSayingNothing()
        {
            // No message to carry is not a reason to say nothing: report the only fact there is.
            var (app, url) = await StartFailingGatewayAsync(502, jsonBody: null);

            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ClientFor(url).RecordHoldAsync(Guid.NewGuid().ToString(), onHold: true));

                Assert.Contains("returned HTTP 502", ex.Message);
            }
            finally
            {
                await app.StopAsync();
            }
        }

        [Fact]
        public async Task Relay_withNonJsonBody_doesNotThrowParsingIt()
        {
            // A proxy or crash can put HTML in front of us. That must degrade to the status line, not blow up
            // with a JSON parse error that buries the real failure.
            var (app, url) = await StartFailingGatewayAsync(502, "<html>502 Bad Gateway</html>");

            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ClientFor(url).RecordHoldAsync(Guid.NewGuid().ToString(), onHold: true));

                Assert.Contains("returned HTTP 502", ex.Message);
            }
            finally
            {
                await app.StopAsync();
            }
        }
    }
}
