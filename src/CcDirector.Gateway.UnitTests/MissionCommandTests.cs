using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the Mission-attach command paths (mission-as-first-class-unit-of-work) through the shared
/// <see cref="SessionCommandExecutor"/>: the <c>attach-mission</c> verb stamps a session's MissionId +
/// cached MissionName (and detaches on a blank id), and a create-time <see cref="NewSessionRequest.MissionId"/>
/// attaches the new session at spawn. Mirrors the set-role tests in <see cref="SessionCommandExecutorTests"/>.
/// </summary>
[Collection("DirectorRoot")]
public sealed class MissionCommandTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path without an
    // installed coding-agent CLI (same approach as SessionCommandExecutorTests).
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session);
    }

    // The Director's store is single-tenant - one machine, one owner - so every record is Local's (#1039).
    private static MissionStore NewMissionStore() =>
        new(Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json"),
            adoptUnattributedAs: Core.Tenancy.TenantId.Local);

    private static DirectorCommand AttachCommand(string sid, Guid? missionId, string? missionName = null) => new()
    {
        CommandId = "am1",
        Verb = "attach-mission",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(
            new SetMissionRequest { MissionId = missionId, MissionName = missionName }, Json),
    };

    /// <summary>An attach that also carries the Gateway's seat decision (issue #2387 review).</summary>
    private static DirectorCommand AttachWithSeat(
        string sid, Guid? missionId, string? missionName, bool moveSeat,
        Guid? runId = null, string? workflowId = null, int? version = null) => new()
    {
        CommandId = "am2",
        Verb = "attach-mission",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new SetMissionRequest
        {
            MissionId = missionId,
            MissionName = missionName,
            MoveSeat = moveSeat,
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            WorkflowVersion = version,
        }, Json),
    };

    private static DirectorCommand CreateCommand(NewSessionRequest req) => new()
    {
        CommandId = "c1",
        Verb = "create",
        SessionId = "",
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task AttachMission_StampsMissionIdAndCachesName_AndReturnsUpdatedSession()
    {
        var (sm, session) = NewSession();
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Session Lifecycle");
            var services = new SessionCommandServices { MissionStore = store };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(session.Id.ToString(), mission.MissionId), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(mission.MissionId, session.MissionId);
            Assert.Equal("Session Lifecycle", session.MissionName); // resolved + cached from the store

            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(mission.MissionId, dto.MissionId);
            Assert.Equal("Session Lifecycle", dto.MissionName);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_UnknownMission_ReturnsBadRequest_Unchanged()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(session.Id.ToString(), Guid.NewGuid()), services);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Null(session.MissionId);
            Assert.Null(session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_BlankMissionId_DetachesSession()
    {
        var (sm, session) = NewSession();
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Session Lifecycle");
            var services = new SessionCommandServices { MissionStore = store };

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(session.Id.ToString(), mission.MissionId), services);
            Assert.Equal(mission.MissionId, session.MissionId);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(session.Id.ToString(), null), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId); // cleared -> detached
            Assert.Null(session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Session Lifecycle");
            var services = new SessionCommandServices { MissionStore = store };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(Guid.NewGuid().ToString(), mission.MissionId), services);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand("not-a-guid", Guid.NewGuid()), services);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: the GATEWAY path on the attach verb, matching what create already does. A mission is a
    // FLEET record whose source of truth is the Gateway; when the Gateway has resolved it inside the caller's
    // own tenant and sends the NAME with the id, the Director stamps it directly. Proof of "no local lookup":
    // the id is not in the Director's store - the store is EMPTY - and the attach still succeeds with the
    // carried name. A local lookup would reject a mission that is real and owned, which is precisely the
    // failure #1548 fixed on the spawn path.
    [Fact]
    public async Task AttachMission_WithMissionNamePresent_StampsDirectly_WithoutStoreLookup()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() }; // empty store
            var carriedId = Guid.NewGuid(); // never created in this store

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), carriedId, "Gateway Native Mission"), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status); // NOT rejected despite the empty store
            Assert.Equal(carriedId, session.MissionId);
            Assert.Equal("Gateway Native Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: ATTACHING IS A MOVE. A session that already carries a mission is re-pointed by the same
    // verb, and the old attachment is gone rather than accumulated. This is the settled rule, not an accident
    // of implementation: a mission's shape is discovered as it runs, so the first classification of a session
    // is always a guess, and a one-way attach would make every wrong guess permanent until the session died.
    [Fact]
    public async Task AttachMission_OnAnAlreadyAttachedSession_MovesItToTheNewMission()
    {
        var (sm, session) = NewSession();
        try
        {
            var store = NewMissionStore();
            var first = store.Create(Core.Tenancy.TenantId.Local, "First Mission");
            var second = store.Create(Core.Tenancy.TenantId.Local, "Second Mission");
            var services = new SessionCommandServices { MissionStore = store };

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), first.MissionId), services);
            Assert.Equal(first.MissionId, session.MissionId);   // the control: it really was on the first

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), second.MissionId), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(second.MissionId, session.MissionId);
            Assert.Equal("Second Mission", session.MissionName);  // the cached name moved with the id
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: a REFUSED attach leaves the session on the mission it already had. Without this, a
    // mistyped mission id would silently detach a correctly-attached session - a failure that looks like
    // nothing happened until somebody goes looking for the pod.
    [Fact]
    public async Task AttachMission_UnknownMission_LeavesAnExistingAttachmentIntact()
    {
        var (sm, session) = NewSession();
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Real Mission");
            var services = new SessionCommandServices { MissionStore = store };

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), mission.MissionId), services);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), Guid.NewGuid()), services);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(mission.MissionId, session.MissionId);
            Assert.Equal("Real Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // ===== The workflow SEAT moves with the mission (issue #2387, review finding) =====
    //
    // A Mission is also a RUN of the built-in "mission" workflow, and a mission-scoped spawn seats the
    // session on that run - which is what pins the conduct its preamble told it to follow. Moving only the
    // mission link left a session DISPLAYED under one mission and GOVERNED by the one it left, taking its
    // conduct from a mission it was no longer in. These cases pin the transition itself, so a later edit
    // cannot quietly go back to moving one without the other.

    [Fact]
    public async Task AttachMission_WhenTheGatewaySaysMoveTheSeat_MovesMissionAndSeatTogether()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };
            var missionA = Guid.NewGuid();
            var runA = Guid.NewGuid();
            var missionB = Guid.NewGuid();
            var runB = Guid.NewGuid();

            // Seated under A, exactly as a mission-scoped spawn leaves a session.
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionA, "Mission A", moveSeat: true, runA, "mission", 8),
                services);
            Assert.Equal(missionA, session.MissionId);
            Assert.Equal(runA, session.WorkflowRunId);      // the control: it really was seated on A's run

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionB, "Mission B", moveSeat: true, runB, "mission", 9),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(missionB, session.MissionId);
            // THE FINDING, asserted directly: the seat is B's, not A's. Before the fix this stayed runA
            // while the mission read B - shown under one mission, governed by the other.
            Assert.Equal(runB, session.WorkflowRunId);
            Assert.Equal(9, session.WorkflowVersion);       // and the PINNED version moved with it

            // The DTO the clients render carries the same pair, so no surface can disagree with the session.
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(missionB, dto.MissionId);
            Assert.Equal(runB, dto.WorkflowRunId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_WhenTheGatewaySaysLeaveTheSeat_MovesTheMissionOnly()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };
            var chosenRun = Guid.NewGuid();
            var missionA = Guid.NewGuid();
            var missionB = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionA, "Mission A", moveSeat: true, chosenRun, "release", 3),
                services);

            // The exception to the rule: a run the caller chose independently was never the mission's to
            // take, so a move must leave it exactly where it is - id, workflow and pinned version.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionB, "Mission B", moveSeat: false),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(missionB, session.MissionId);      // the mission moved
            Assert.Equal(chosenRun, session.WorkflowRunId); // the seat did not
            Assert.Equal("release", session.WorkflowId);
            Assert.Equal(3, session.WorkflowVersion);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_Detach_ClearsTheMissionsSeatWithIt()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };
            var mission = Guid.NewGuid();
            var run = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), mission, "Mission A", moveSeat: true, run, "mission", 8),
                services);
            Assert.Equal(run, session.WorkflowRunId);

            // Detach: a session that has LEFT a mission cannot still be governed by that mission's run.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), null, null, moveSeat: true),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId);
            Assert.Null(session.WorkflowRunId);
            Assert.Null(session.WorkflowId);
            Assert.Null(session.WorkflowVersion);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_Detach_LeavesAnIndependentlyChosenSeatAlone()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };
            var chosenRun = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), Guid.NewGuid(), "Mission A", moveSeat: true, chosenRun, "release", 3),
                services);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), null, null, moveSeat: false),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId);                 // detached from the mission
            Assert.Equal(chosenRun, session.WorkflowRunId); // but still on the run it was put on
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_WithoutASeatDecision_TouchesTheSeatAtAll()
    {
        // The compatibility case, and the one that keeps the two decisions separable: a payload that says
        // nothing about the seat must not clear it. Otherwise every caller that has not been taught about
        // seats would silently unseat the sessions it attaches.
        var (sm, session) = NewSession();
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Local mission");
            var services = new SessionCommandServices { MissionStore = store };
            var run = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), Guid.NewGuid(), "Mission A", moveSeat: true, run, "mission", 8),
                services);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), mission.MissionId), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(mission.MissionId, session.MissionId);
            Assert.Equal(run, session.WorkflowRunId);   // untouched, because nothing asked for it to move
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Create_WithMissionId_AttachesNewSessionAtSpawn()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Session Lifecycle");
            var services = new SessionCommandServices { MissionStore = store };

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "mission-create-test",
                MissionId = mission.MissionId,
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(mission.MissionId, dto.MissionId);
            Assert.Equal("Session Lifecycle", dto.MissionName);

            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            var session = sm.GetSession(sid);
            Assert.NotNull(session);
            Assert.Equal(mission.MissionId, session.MissionId);
            Assert.Equal("Session Lifecycle", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Gateway Cleanup mission (Wave 4b): when the create request carries BOTH a MissionId and a MissionName
    // (the GATEWAY path - the Gateway already validated the mission against its own store), the Director
    // stamps the attachment DIRECTLY with no local-store lookup. Proof of "no lookup": the id is NOT in the
    // store here (in fact the store is EMPTY), yet the create succeeds and stamps the carried name - a local
    // lookup would have rejected it as unknown.
    [Fact]
    public async Task Create_WithMissionNamePresent_StampsDirectly_WithoutStoreLookup()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() }; // empty store
            var carriedId = Guid.NewGuid(); // never created in the store

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "gateway-mission-create",
                MissionId = carriedId,
                MissionName = "Gateway Native Mission", // resolved+validated by the Gateway already
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status); // NOT rejected despite the empty store
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(carriedId, dto.MissionId);
            Assert.Equal("Gateway Native Mission", dto.MissionName); // stamped from the request, not the store

            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            var session = sm.GetSession(sid);
            Assert.NotNull(session);
            Assert.Equal(carriedId, session.MissionId);
            Assert.Equal("Gateway Native Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Gateway Cleanup mission (Wave 4b): the TRANSITIONAL BRIDGE. When the create request carries a MissionId
    // but a BLANK MissionName (an old caller hitting the Director's POST /sessions directly for a
    // Director-store mission), the Director resolves the name from its OWN store - so the stamped name comes
    // from the store, not the request. Proof: the store name differs from anything on the request.
    [Fact]
    public async Task Create_WithMissionNameAbsent_ResolvesNameFromLocalStore()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var store = NewMissionStore();
            var mission = store.Create(Core.Tenancy.TenantId.Local, "Name From Director Store");
            var services = new SessionCommandServices { MissionStore = store };

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "bridge-mission-create",
                MissionId = mission.MissionId,
                MissionName = null, // blank -> the transitional local-store lookup resolves the name
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(mission.MissionId, dto.MissionId);
            Assert.Equal("Name From Director Store", dto.MissionName); // resolved from the store
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Create_WithUnknownMissionId_ReturnsBadRequest_NoSessionCreated()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var services = new SessionCommandServices { MissionStore = NewMissionStore() };
            var before = sm.ListSessions().Count;

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "bad-mission",
                MissionId = Guid.NewGuid(), // never created in the store
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, services);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(before, sm.ListSessions().Count); // rejected before creation - no orphan
        }
        finally { sm.Dispose(); }
    }
}
