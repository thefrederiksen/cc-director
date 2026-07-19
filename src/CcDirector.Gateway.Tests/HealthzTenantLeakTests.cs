using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (session-serving PR2): <c>/healthz</c> reports NO per-tenant facts on the hosted
/// Gateway.
///
/// <c>/healthz</c> is deliberately PUBLIC - it is the unauthenticated liveness probe the Director's
/// connectivity self-test, the gateway endpoint selector, and the settings gateway test all dial - so it
/// carries no credential and therefore has NO TENANT. Its Directors/Sessions counts are fleet-GLOBAL
/// aggregates, so on a hosted Gateway an anonymous caller was reading a count over EVERY account's Directors.
/// That is not fixable by making the count tenant-aware: a request with no tenant has no correct number to
/// print. So on hosted the aggregate is not computed at all - deny-by-default applies to metrics too.
///
/// POSITIVE CONTROL / self-host-untouched: the same handler on a NON-hosted Gateway still reports the counts,
/// which is what proves the hosted assertion is about the tenancy branch and not about a Gateway that simply
/// has no Directors registered.
///
/// Revert-prove: delete the <c>tenantBoundary?.IsHosted == true</c> branch from the <c>/healthz</c> handler in
/// <c>GatewayEndpoints.Map</c> and <see cref="Hosted_healthz_reports_no_fleet_counts"/> goes RED - the hosted
/// probe reports the registered Director again.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe.
/// </summary>
public sealed class HealthzTenantLeakTests
{
    private const string Token = "test-token";

    [Fact]
    public async Task Hosted_healthz_reports_no_fleet_counts()
    {
        var health = await ProbeHealthz(hosted: true);

        // The Gateway HAS a registered Director (see RunAsync) - the hosted probe simply must not say so.
        Assert.Equal("ok", health.Status);
        Assert.Equal(0, health.Directors);
        Assert.Equal(0, health.Sessions);
        // Liveness is still answered: a probe needs to know it is alive and what version it is.
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
    }

    [Fact]
    public async Task Selfhost_healthz_still_reports_its_fleet_counts()
    {
        // POSITIVE CONTROL for the test above, and the self-host-untouched proof: identical arrangement, the
        // ONLY difference being CC_GATEWAY_HOSTED, and the counts are still served.
        var health = await ProbeHealthz(hosted: false);

        Assert.Equal("ok", health.Status);
        Assert.Equal(1, health.Directors);
    }

    private static async Task<HealthDto> ProbeHealthz(bool hosted)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-hz-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        try
        {
            await gateway.StartAsync();
            gateway.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-health",
                TailnetEndpoint = "http://127.0.0.1:1/",
                MachineName = "MH",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            // NO credential: /healthz is public, which is exactly why it must not carry per-tenant facts.
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var resp = await http.GetAsync("healthz");
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<HealthDto>()
                   ?? throw new InvalidOperationException("healthz returned no body");
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
            try { if (Directory.Exists(instancesDir)) Directory.Delete(instancesDir, true); }
            catch { /* best-effort */ }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
