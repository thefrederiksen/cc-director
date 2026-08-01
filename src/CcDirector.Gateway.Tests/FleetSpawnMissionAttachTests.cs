using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1548, at the endpoint. Reproduces the reported failure exactly: `cc-devthrottle session spawn
/// &lt;repo&gt; --mission &lt;id&gt;` sends POST /fleet/spawn carrying a mission ID and NOTHING ELSE - the CLI
/// has no way to send the name - and the spawn was rejected with "unknown mission '&lt;id&gt;'. Create it
/// first with POST /missions." for a mission that already existed at the Gateway.
///
/// The local spawn leg never asked the Gateway, so the request reached the Director floor with the name
/// blank and fell through to the floor's TEMPORARY local-store bridge, which looked in the Director's own
/// missions.json - the wrong store. The remote leg never had the bug: it leaves through the Gateway, which
/// stamps the name on the way out.
///
/// This drives the REAL Director Control API against a REAL Gateway stub over a REAL loopback HTTP
/// connection, so the whole path - endpoint, GatewayClient, wire, floor - runs. Before the fix the first
/// fact below returns 400 "unknown mission"; after it, the session is born carrying the mission NAME.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetSpawnMissionAttachTests : IAsyncLifetime
{
    private static readonly Guid KnownMissionId = Guid.NewGuid();
    private const string KnownMissionName = "Stable Release";

    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _gatewayStub = null!;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private string _repoDir = null!;

    public FleetSpawnMissionAttachTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-spawnmission-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // A Gateway stub that knows exactly ONE mission - the source of truth the Director must consult.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _gatewayStub = builder.Build();
        _gatewayStub.MapGet("/missions/{mid}", (string mid) =>
            Guid.TryParse(mid, out var id) && id == KnownMissionId
                ? Results.Json(new MissionDto { MissionId = KnownMissionId, MissionName = KnownMissionName })
                : Results.NotFound(new { error = "mission not found" }));
        await _gatewayStub.StartAsync();
        var gatewayUrl = _gatewayStub.Urls.First();

        // Point this Director at the stub. GatewayConfig.Load reads config.json under CC_DIRECTOR_ROOT, so
        // this is the same wiring a real install uses - no test-only seam.
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"url\":\"" + gatewayUrl + "\"}}");

        _repoDir = Path.Combine(Path.GetTempPath(), "ccd-spawnmission-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = DirectorTestClient.Admin(port);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        await _gatewayStub.StopAsync();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_repoDir)) Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }

    /// <summary>A spawn body shaped exactly like the CLI's: a mission ID, never a name.</summary>
    private NewSessionRequest CliSpawnBody(Guid missionId) => new()
    {
        RepoPath = _repoDir,
        Agent = "RawCli",
        Command = "cmd",
        CommandArgs = "/k",
        MissionId = missionId,
    };

    [Fact]
    public async Task Local_spawn_withMissionIdOnly_resolvesTheNameFromTheGateway_andAttaches()
    {
        // The exact #1548 reproduction. This returned 400 "unknown mission" before the fix.
        var resp = await _client.PostAsJsonAsync("fleet/spawn", CliSpawnBody(KnownMissionId));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(dto);
        Assert.Equal(KnownMissionId, dto!.MissionId);

        // The point of the whole issue: the session is born carrying the mission NAME, resolved from the
        // Gateway's store, so it is genuinely bound into the pod rather than rejected against the wrong one.
        Assert.Equal(KnownMissionName, dto.MissionName);

        if (dto.SessionId is not null)
            await _client.DeleteAsync($"sessions/{dto.SessionId}");
    }

    [Fact]
    public async Task Local_spawn_withMissionUnknownToTheGateway_saysSo_inPlainEnglish()
    {
        // A mission the Gateway genuinely does not have still fails - but now it fails against the RIGHT
        // store, and the message points at the command that lists them instead of telling the human to
        // create something that may already exist.
        var resp = await _client.PostAsJsonAsync("fleet/spawn", CliSpawnBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("The Gateway has no mission with that id", body);
        Assert.Contains("cc-devthrottle mission list", body);
    }

    [Fact]
    public async Task Local_spawn_withNoMission_isUnaffected()
    {
        // The no-mission path must not have acquired a Gateway round-trip.
        var body = CliSpawnBody(KnownMissionId);
        body.MissionId = null;

        var resp = await _client.PostAsJsonAsync("fleet/spawn", body);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.MissionId);
        Assert.Null(dto.MissionName);

        if (dto.SessionId is not null)
            await _client.DeleteAsync($"sessions/{dto.SessionId}");
    }
}
