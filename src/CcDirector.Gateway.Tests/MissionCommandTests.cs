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
