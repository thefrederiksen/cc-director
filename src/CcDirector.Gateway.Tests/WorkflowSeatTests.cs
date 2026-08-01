using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Workflows mission, phase 5b: seated sessions, proven over the REAL local-spawn path (the
/// FleetSpawnMissionAttachTests harness - a real Director Control API against a Gateway stub over
/// loopback HTTP). The claims: a mission-scoped spawn AUTO-SEATS the new session on the mission's
/// workflow run (run id + workflow id + PINNED version stamped on the session, straight from the
/// Gateway, never resolved by the Director); the seated session's fleet preamble carries the seat
/// paragraph telling the agent to fetch its conduct at exactly the pinned version and to STOP if the
/// fetch fails; the new session is recorded as a run PARTICIPANT on the Gateway (the persisted
/// run-to-session membership #1771 reads); an unknown explicit run id is refused in plain English;
/// and an unseated spawn is byte-identical to before.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WorkflowSeatTests : IAsyncLifetime
{
    private static readonly Guid KnownMissionId = Guid.NewGuid();
    private static readonly Guid KnownRunId = Guid.NewGuid();
    private const string KnownMissionName = "Seat Proof";
    private const int PinnedVersion = 3;

    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _gatewayStub = null!;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private string _repoDir = null!;

    /// <summary>Participant PATCH bodies the stub captured, keyed by run id.</summary>
    private readonly List<(Guid RunId, string Body)> _participantPatches = new();

    public WorkflowSeatTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-seat-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    private static WorkflowRunDto KnownRun() => new()
    {
        Id = KnownRunId,
        WorkflowId = "mission",
        WorkflowVersion = PinnedVersion,
        ContentHash = "hash-3",
        Name = KnownMissionName,
        MissionId = KnownMissionId,
    };

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _gatewayStub = builder.Build();
        _gatewayStub.MapGet("/missions/{mid}", (string mid) =>
            Guid.TryParse(mid, out var id) && id == KnownMissionId
                ? Results.Json(new MissionDto { MissionId = KnownMissionId, MissionName = KnownMissionName })
                : Results.NotFound(new { error = "mission not found" }));
        _gatewayStub.MapGet("/gateway/workflow-runs", (Guid? missionId) =>
            Results.Json(new
            {
                runs = missionId == KnownMissionId
                    ? new[] { KnownRun() }
                    : Array.Empty<WorkflowRunDto>(),
            }));
        _gatewayStub.MapGet("/gateway/workflow-runs/{id:guid}", (Guid id) =>
            id == KnownRunId
                ? Results.Json(KnownRun())
                : Results.NotFound(new { error = $"no workflow run '{id}'" }));
        _gatewayStub.MapPatch("/gateway/workflow-runs/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            _participantPatches.Add((id, await reader.ReadToEndAsync()));
            return Results.Json(KnownRun());
        });
        await _gatewayStub.StartAsync();
        var gatewayUrl = _gatewayStub.Urls.First();

        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            "{\"gateway\":{\"url\":\"" + gatewayUrl + "\"}}");

        _repoDir = Path.Combine(Path.GetTempPath(), "ccd-seat-repo-" + Guid.NewGuid().ToString("N"));
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

    private NewSessionRequest CliSpawnBody() => new()
    {
        RepoPath = _repoDir,
        Agent = "RawCli",
        Command = "cmd",
        CommandArgs = "/k",
        Role = "Architect",
    };

    [Fact]
    public async Task Mission_spawn_autoSeats_stampsThePin_recordsTheParticipant_andBriefsTheAgent()
    {
        var body = CliSpawnBody();
        body.MissionId = KnownMissionId;

        var resp = await _client.PostAsJsonAsync("fleet/spawn", body);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(dto);

        // The seat: run id + workflow id + PINNED version, stamped from the Gateway's answer.
        Assert.Equal(KnownRunId, dto!.WorkflowRunId);
        Assert.Equal("mission", dto.WorkflowId);
        Assert.Equal(PinnedVersion, dto.WorkflowVersion);

        // The participant: the Gateway received the membership record with the canonical session id.
        var patch = Assert.Single(_participantPatches);
        Assert.Equal(KnownRunId, patch.RunId);
        Assert.Contains(dto.SessionId!, patch.Body);
        Assert.Contains("RawCli", patch.Body);
        Assert.Contains("Architect", patch.Body);
        Assert.Contains(Environment.MachineName, patch.Body,
            StringComparison.OrdinalIgnoreCase);

        // The briefing: the seated session's preamble tells the agent its seat, the PINNED fetch
        // command, and the fail-closed rule - regardless of agent kind, because it rides the same
        // preamble every agent family receives.
        var preamble = await _client.GetStringAsync($"sessions/{dto.SessionId}/fleet-preamble");
        Assert.Contains("[Workflow seat]", preamble);
        Assert.Contains("seated as Architect on the 'mission' workflow", preamble);
        Assert.Contains($"cc-devthrottle workflow instructions mission --version {PinnedVersion}", preamble);
        Assert.Contains("STOP and report", preamble);

        await _client.DeleteAsync($"sessions/{dto.SessionId}");
    }

    [Fact]
    public async Task An_unknown_explicit_run_id_is_refused_inPlainEnglish()
    {
        var body = CliSpawnBody();
        body.WorkflowRunId = Guid.NewGuid();

        var resp = await _client.PostAsJsonAsync("fleet/spawn", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown workflow run", text);
        Assert.Contains("cc-devthrottle workflow runs", text);
        Assert.Empty(_participantPatches);
    }

    [Fact]
    public async Task An_unseated_spawn_isUnaffected_andCarriesNoSeatParagraph()
    {
        var resp = await _client.PostAsJsonAsync("fleet/spawn", CliSpawnBody());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.WorkflowRunId);
        Assert.Null(dto.WorkflowId);
        Assert.Null(dto.WorkflowVersion);
        Assert.Empty(_participantPatches);

        var preamble = await _client.GetStringAsync($"sessions/{dto.SessionId}/fleet-preamble");
        Assert.DoesNotContain("[Workflow seat]", preamble);

        await _client.DeleteAsync($"sessions/{dto.SessionId}");
    }
}
