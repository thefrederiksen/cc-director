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

    private static MissionStore NewMissionStore() =>
        new(Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json"));

    private static DirectorCommand AttachCommand(string sid, Guid? missionId) => new()
    {
        CommandId = "am1",
        Verb = "attach-mission",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new SetMissionRequest { MissionId = missionId }, Json),
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
            var mission = store.Create("Session Lifecycle");
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
            var mission = store.Create("Session Lifecycle");
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
            var mission = store.Create("Session Lifecycle");
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

    [Fact]
    public async Task Create_WithMissionId_AttachesNewSessionAtSpawn()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var store = NewMissionStore();
            var mission = store.Create("Session Lifecycle");
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
            var mission = store.Create("Name From Director Store");
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
