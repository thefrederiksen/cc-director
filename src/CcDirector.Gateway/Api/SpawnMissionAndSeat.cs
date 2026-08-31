using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one place a spawn's MISSION and WORKFLOW SEAT are resolved before the create leaves the Gateway.
///
/// A session can be started through two doors - POST /machines/{machine}/sessions ("some Director on that
/// computer") and POST /directors/{id}/sessions ("this exact Director", which is what an unqualified
/// <c>cc-devthrottle session spawn</c> uses) - and BOTH must hand the Director a finished answer, because
/// the Director no longer holds missions or runs and cannot resolve either one itself.
///
/// THIS EXISTS BECAUSE THE TWO DOORS DRIFTED, and the drift was a total outage of mission-scoped spawning
/// (issue #2629). The machine door resolved the mission name; the Director door forwarded the request
/// verbatim. So a create arrived carrying a mission id and no NAME, which the Director read as "an old
/// caller naming a mission in my own local store", looked the id up in a stale per-machine missions.json,
/// and refused a mission that was real, active and listed - advising the caller to create something that
/// already existed. The seat drifted the same way and more quietly: the Director door seated nothing, so a
/// mission-scoped session started there would have run without the conduct its mission pins, and with no
/// membership row for governance to read.
///
/// Two copies of a rule drift apart by default; one cannot. Anything a spawn door must do to a create
/// request before dispatching it belongs HERE, and a door that skips this helper is the same defect again.
/// </summary>
internal static class SpawnMissionAndSeat
{
    /// <summary>
    /// Resolve the mission NAME and the workflow SEAT onto <paramref name="req"/>, in the caller's own
    /// tenant. Returns false when the request must be refused, with <paramref name="error"/> holding the
    /// answer to return; true otherwise, with <paramref name="seatRun"/> naming the run the session was
    /// seated on (null = unseated, which is a normal outcome and not a failure).
    ///
    /// <paramref name="route"/> names the calling door in the log, so a rejection says which one it came
    /// through.
    /// </summary>
    internal static bool TryResolve(
        NewSessionRequest req,
        TenantId tenant,
        Core.Sessions.MissionStore? missions,
        Workflows.WorkflowRunStore? workflowRuns,
        string route,
        out WorkflowRunDto? seatRun,
        out IResult? error)
    {
        seatRun = null;
        error = null;

        // Gateway Cleanup mission (Wave 4b): a mission-scoped spawn is validated against the Gateway's OWN
        // mission store (the source of truth) and the resolved NAME is stamped onto the create request, so
        // the Director stamps the attachment directly with no local-store lookup. Reject an unknown mission
        // here rather than forwarding it to a Director that no longer owns mission validation.
        if (req.MissionId is Guid spawnMissionId && missions is not null)
        {
            // #1039: resolve the mission in the CALLING tenant. This lookup was by bare id, so naming
            // another account's mission id here stamped that account's mission NAME - free text a person
            // typed - onto the caller's own session. It is the same disclosure GET /missions/{mid} was,
            // reached through the spawn route instead, and the issue does not name it.
            var mission = missions.Get(tenant, spawnMissionId);
            if (mission is null)
            {
                FileLog.Write($"[SpawnMissionAndSeat] {route}: unknown mission {spawnMissionId}");
                error = Results.BadRequest(new { error = $"unknown mission '{spawnMissionId}'. Create it first with POST /missions." });
                return false;
            }
            req.MissionName = mission.MissionName;
        }

        // Workflows mission (phase 5b): resolve the seat. An EXPLICIT run id must exist; a mission-scoped
        // spawn with no explicit run auto-seats onto the mission's newest run (the one POST /missions
        // opened). The run's workflow id + pinned version ride the create request so the Director stamps
        // the seat with no lookup of its own - and the seated session's conduct is pinned to the run's
        // version, never a moving head.
        // ON THE RUN LOOKUP AND TENANCY, because it reads like a hole and is not - a reviewer raised it as
        // one. The run id here IS caller-supplied, unlike the mission id above it is not resolved against a
        // tenant argument, and a caller owning the target Director does not by itself prove they own the
        // run. What closes it is the store, not this code: WorkflowRunEntity is a tenant-scoped entity
        // (GatewayDbContext.ApplyTenantScope) carrying a global query filter TenantId == ActiveTenant, and
        // WorkflowRunStore.Get opens its context from the AMBIENT tenant - the caller's own on hosted, set
        // by the request middleware. Another account's run id therefore reads back as nothing and is
        // refused below as unknown, which is also how an id that never existed is answered, so the two
        // cannot be told apart. Do not "fix" this by passing the tenant in: that would imply the store is
        // unfiltered, which is the belief worth not planting.
        if (workflowRuns is not null)
        {
            if (req.WorkflowRunId is Guid explicitRunId)
            {
                seatRun = workflowRuns.Get(explicitRunId);
                if (seatRun is null)
                {
                    FileLog.Write($"[SpawnMissionAndSeat] {route}: unknown workflow run {explicitRunId}");
                    error = Results.BadRequest(new { error = $"unknown workflow run '{explicitRunId}'." });
                    return false;
                }
            }
            else if (req.MissionId is Guid seatMissionId)
            {
                seatRun = workflowRuns.List(missionId: seatMissionId, limit: 1).FirstOrDefault();
            }

            if (seatRun is not null && !seatRun.WorkflowEnabled)
            {
                // The owner turned this workflow OFF: no new seats. The spawn proceeds unseated - the
                // owner's switch, honestly applied and loudly logged.
                FileLog.Write($"[SpawnMissionAndSeat] {route}: workflow '{seatRun.WorkflowId}' is OFF - spawning UNSEATED");
                seatRun = null;
            }
            if (seatRun is not null)
            {
                req.WorkflowRunId = seatRun.Id;
                req.WorkflowId = seatRun.WorkflowId;
                req.WorkflowVersion = seatRun.WorkflowVersion;
            }
        }

        return true;
    }

    /// <summary>
    /// Record the new session as a participant of the run it was seated on - the persisted
    /// run-to-session membership (#1771). The session id is the canonical fleet GUID governance joins
    /// effort on. Two guards, both from inspection findings:
    ///  - Record ONLY when the Director's reply proves the seat actually landed. An older Director
    ///    (rolling upgrade) ignores the seat fields and returns a DTO without them; recording membership
    ///    for a session whose agent never received its conduct would be a governance lie.
    ///  - The spawn has already SUCCEEDED; a participant-write failure is reported loudly in the log,
    ///    never converted into an HTTP failure the caller would retry into a second session.
    /// </summary>
    internal static void RecordParticipant(
        WorkflowRunDto? seatRun,
        Workflows.WorkflowRunStore? workflowRuns,
        NewSessionRequest req,
        SessionDto dto,
        string machine,
        string route)
    {
        if (seatRun is null || workflowRuns is null || string.IsNullOrWhiteSpace(dto.SessionId))
            return;

        if (dto.WorkflowRunId != seatRun.Id)
        {
            FileLog.Write($"[SpawnMissionAndSeat] {route}: Director did NOT stamp the seat (returned " +
                          $"run={dto.WorkflowRunId?.ToString() ?? "none"}; it likely predates seated " +
                          "sessions). Session started UNSEATED; no participant recorded.");
            return;
        }

        try
        {
            workflowRuns.Patch(seatRun.Id, new PatchWorkflowRunRequest
            {
                AddParticipants = new List<WorkflowRunParticipantDto>
                {
                    new()
                    {
                        SessionId = dto.SessionId,
                        AgentKind = req.Agent,
                        Role = req.Role ?? "",
                        Machine = machine,
                    },
                },
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpawnMissionAndSeat] {route}: run-participant record FAILED for session " +
                          $"{dto.SessionId} on run {seatRun.Id}: {ex.Message}. The session is seated and " +
                          "running; governance is missing this membership row.");
        }
    }
}
