using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director-startup telemetry route over the WIRE, behind the REAL authentication gate, on a HOSTED
/// Gateway (an <see cref="AsyncLocalTenantContext"/> so <see cref="HostedTenantBoundary.IsHosted"/> is true),
/// with two callers whose enrolled device keys bind to different tenants (audit MTR gap C).
///
/// Two endpoint-specific properties the queue tests cannot see:
/// <list type="bullet">
///   <item>The tenant is the SERVER-RESOLVED one, deny-by-default: a caller whose key binds to NO tenant is
///     refused 403 on hosted, never queued under a guessed tenant.</item>
///   <item>Director-id ownership: a caller may create a startup observation ONLY for a director id its own
///     tenant PROVABLY owns. Its own id is 202; ANOTHER tenant's known id (the "false startup observation
///     for B" the audit describes), an as-yet-UNKNOWN id, and a blank id are all 403 - a caller may not post
///     an observation for a director id it does not own.</item>
/// </list>
/// The gate is in the pipeline on purpose (<see cref="NoCredential_IsRejected_SoTheseTestsRunBehindTheGate"/>
/// keeps that honest); status codes are asserted directly, never inferred from a body.
/// </summary>
public sealed class DirectorStartupTelemetryTenantTests : IAsyncLifetime
{
    private const string SharedMachineToken = "shared-machine-token-startup-tenant";
    private const string TenantA = "tenant-a-guid-aaaaaaaaaaaaaaaa";
    private const string TenantB = "tenant-b-guid-bbbbbbbbbbbbbbbb";

    private readonly string _registryPath = Path.Combine(Path.GetTempPath(), $"startup-tenant-devices-{Guid.NewGuid():N}.json");
    private readonly string _queuePath = Path.Combine(Path.GetTempPath(), $"startup-tenant-queue-{Guid.NewGuid():N}.json");
    private readonly string _directorsDir = Path.Combine(Path.GetTempPath(), $"startup-tenant-directors-{Guid.NewGuid():N}");

    private WebApplication _app = null!;
    private TelemetryRetryQueue _queue = null!;
    private string _baseAddress = "";

    private string _keyA = "";       // binds to tenant A
    private string _keyB = "";       // binds to tenant B
    private string _keyUnbound = ""; // enrolled but never bound to a tenant

    public async Task InitializeAsync()
    {
        var devices = new DeviceRegistry(_registryPath);
        _keyA = devices.Register("device-a", "PHONE-A", "android", "phone").DeviceKey;
        _keyB = devices.Register("device-b", "PHONE-B", "android", "phone").DeviceKey;
        _keyUnbound = devices.Register("device-u", "PHONE-U", "android", "phone").DeviceKey;
        devices.SetAccountBinding("device-a", "subject-a", TenantA);
        devices.SetAccountBinding("device-b", "subject-b", TenantB);
        // device-u is left unbound on purpose - on hosted it resolves to NO tenant (a deny).

        // Tenant B owns Director "dir-b". Tenant A owns "dir-a". Registered via the tunnel Hello path, which
        // is the only path that keys a Director to a real account tenant.
        var directors = new DirectorRegistry(_directorsDir);
        directors.RegisterFromStream("dir-b", "MACHINE-B", "userb", "1.0", 4321, DateTime.UtcNow, new TenantId(TenantB));
        directors.RegisterFromStream("dir-a", "MACHINE-A", "usera", "1.0", 1234, DateTime.UtcNow, new TenantId(TenantA));

        // Hosted boundary: a real AsyncLocalTenantContext makes IsHosted true and the tenant resolve from the
        // authenticated device key's binding.
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), devices);
        Assert.True(boundary.IsHosted); // this suite really is exercising the hosted path

        // No cloud startup URL configured -> the endpoint records only (202) and never forwards; the
        // deny/forgery decisions all happen before the queue, so this keeps the test to status-code checks.
        Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, null);

        _queue = new TelemetryRetryQueue(_queuePath, new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            retryInterval: TimeSpan.FromMilliseconds(100));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var port = AllocateFreePort();
        _app.Urls.Add($"http://127.0.0.1:{port}");

        // The real host-wide gate, exactly as GatewayHost installs it - so the device key that the boundary
        // reads is the one the gate accepted, never a second re-parse of the request.
        var requireToken = new AuthMiddleware.RequireToken { Token = SharedMachineToken, Devices = devices };
        _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));

        DirectorStartupTelemetryEndpoint.Map(_app, _queue, boundary, directors);
        _queue.StartFlushing();
        await _app.StartAsync();
        _baseAddress = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _queue.DisposeAsync();
        try { if (File.Exists(_registryPath)) File.Delete(_registryPath); } catch { }
        try { if (File.Exists(_queuePath)) File.Delete(_queuePath); } catch { }
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpClient Client() => new() { BaseAddress = new Uri(_baseAddress) };

    private async Task<HttpStatusCode> PostStartupAsync(string? bearerKey, string directorId)
    {
        using var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/telemetry/director-startup")
        {
            Content = JsonContent.Create(new { director_id = directorId, machine_name = "M", app_version = "1.0" }),
        };
        if (bearerKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task NoCredential_IsRejected_SoTheseTestsRunBehindTheGate()
        => Assert.Equal(HttpStatusCode.Unauthorized, await PostStartupAsync(bearerKey: null, "dir-a"));

    [Fact]
    public async Task UnboundKey_ResolvesNoTenantOnHosted_IsDenied()
        => Assert.Equal(HttpStatusCode.Forbidden, await PostStartupAsync(_keyUnbound, "dir-a"));

    [Fact]
    public async Task CallerPostingAnotherTenantsKnownDirectorId_IsRejected()
        // Tenant A forging tenant B's known director id - the exact "false startup observation for B".
        => Assert.Equal(HttpStatusCode.Forbidden, await PostStartupAsync(_keyA, "dir-b"));

    [Fact]
    public async Task CallerPostingItsOwnDirectorId_IsAccepted()
        => Assert.Equal(HttpStatusCode.Accepted, await PostStartupAsync(_keyA, "dir-a"));

    [Fact]
    public async Task CallerPostingAnUnknownDirectorId_IsRejected_NotProvablyOwned()
        // An id registered to nobody yet is not PROVABLY the caller's, so it is refused rather than accepted:
        // the caller may not create a startup observation for a director id it cannot prove it owns. This is
        // the audit fix - the previous behaviour accepted an unknown id, which let a caller mint an
        // observation for any id at all.
        => Assert.Equal(HttpStatusCode.Forbidden, await PostStartupAsync(_keyA, "dir-not-registered-yet"));

    [Fact]
    public async Task CallerPostingABlankDirectorId_IsRejected()
        // A blank director id can never be provably owned, so it is refused (not recorded, not enqueued).
        => Assert.Equal(HttpStatusCode.Forbidden, await PostStartupAsync(_keyA, ""));

    [Fact]
    public async Task TheOtherDirection_TenantBMayNotForgeTenantAsId()
        => Assert.Equal(HttpStatusCode.Forbidden, await PostStartupAsync(_keyB, "dir-a"));
}
