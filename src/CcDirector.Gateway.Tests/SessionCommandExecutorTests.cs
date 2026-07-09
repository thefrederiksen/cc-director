using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1177 (Phase 1): unit tests for the shared <see cref="SessionCommandExecutor"/> - the single
/// command core the Director's REST endpoints and its Gateway stream down-channel both call. Verifies the
/// <c>prompt</c> verb's guards and effect against a real <see cref="SessionManager"/> holding an embedded
/// buffer-only session (reusing <see cref="ExecuteActionTestBackend"/>), so the executor's behaviour is
/// asserted the same way the REST endpoint's was - by the exact bytes reaching the session buffer.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionCommandExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand PromptCommand(string sessionId, PromptRequest req) => new()
    {
        CommandId = "cmd-1",
        Verb = "prompt",
        SessionId = sessionId,
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task DispatchAsync_Prompt_DeliversTextAndReturnsAcceptedResponse()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "hello", AppendEnter = false });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.True(result.Ok);
            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("cmd-1", result.CommandId);
            Assert.NotNull(result.BodyJson);

            var response = JsonSerializer.Deserialize<PromptResponse>(result.BodyJson, Json);
            Assert.NotNull(response);
            Assert.True(response.Accepted);

            // AppendEnter=false => raw SendInput, so the exact bytes land in the session buffer.
            Assert.NotNull(backend.Buffer);
            var written = Encoding.UTF8.GetString(backend.Buffer.DumpAll());
            Assert.Contains("hello", written);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_ExitedSession_ReturnsConflict()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            backend.RaiseProcessExited(1); // drive Session.Status to Exited via the real backend event
            Assert.True(session.Status is SessionStatus.Exited or SessionStatus.Failed);

            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.False(result.Ok);
            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = PromptCommand("not-a-guid", new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = PromptCommand(Guid.NewGuid().ToString(), new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_EmptyText_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    // ---------- interrupt ----------

    [Fact]
    public async Task DispatchAsync_Interrupt_ClaudeSession_ReturnsOk()
    {
        var (sm, session, _) = NewSession(); // default AgentKind is ClaudeCode, whose driver interrupts cleanly
        try
        {
            var command = new DirectorCommand { CommandId = "i1", Verb = "interrupt", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("i1", result.CommandId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_DriverRefuses_ReturnsConflict()
    {
        var (sm, session, _) = NewSession();
        session.AgentKind = Core.Agents.AgentKind.Pi; // pi's driver has no safe hard interrupt -> NotSupportedException
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- escape ----------

    [Fact]
    public async Task DispatchAsync_Escape_ClaudeSession_ReturnsOk()
    {
        var (sm, session, _) = NewSession(); // ClaudeCode's driver soft-cancels cleanly
        try
        {
            var command = new DirectorCommand { CommandId = "e1", Verb = "escape", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("e1", result.CommandId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Escape_DriverRefuses_ReturnsConflict()
    {
        var (sm, session, _) = NewSession();
        session.AgentKind = Core.Agents.AgentKind.Copilot; // copilot's soft-cancel is not live-verified -> NotSupportedException
        try
        {
            var command = new DirectorCommand { Verb = "escape", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Escape_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "escape", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- hold ----------

    [Fact]
    public async Task DispatchAsync_Hold_EmptyPayload_DefaultsToOnHoldTrue()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand { CommandId = "h1", Verb = "hold", SessionId = session.Id.ToString(), PayloadJson = "" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.True(session.OnHold);
            var response = JsonSerializer.Deserialize<HoldResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(response);
            Assert.True(response.OnHold);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_OnHoldFalse_UnparksSession()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.OnHold = true; // start held
            var command = new DirectorCommand
            {
                Verb = "hold",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new HoldRequest { OnHold = false }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.False(session.OnHold);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "hold", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "hold", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- kill ----------

    [Fact]
    public async Task DispatchAsync_Kill_ExistingSession_KillsAndRemoves()
    {
        var (sm, session, _) = NewSession();
        var id = session.Id;
        try
        {
            var command = new DirectorCommand { CommandId = "k1", Verb = "kill", SessionId = id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("k1", result.CommandId);
            Assert.Null(sm.GetSession(id)); // removed from tracking
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_EscalatesOnTheFleetGraceWindow_FastByDefault()
    {
        // The kill verb is the FLEET/remote stop path, so it force-escalates on the shorter FleetKillGraceMs
        // window (1500ms by default), NOT the 5000ms desktop GracefulShutdownTimeoutSeconds.
        var (sm, session, backend) = NewSession();
        try
        {
            var command = new DirectorCommand { CommandId = "k-fast", Verb = "kill", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.True(result.Ok);
            Assert.Equal(1500, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_HonorsAConfiguredFleetGrace()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions { FleetKillGraceMs = 800 });
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        try
        {
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { Verb = "kill", SessionId = session.Id.ToString() });

            Assert.Equal(800, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_DisabledFleetGrace_FallsBackToStandardWindow()
    {
        // FleetKillGraceMs disabled (null) -> the fleet stop uses the standard GracefulShutdownTimeoutSeconds
        // (2s here), byte-identical to before the faster-STOP change.
        var sm = new SessionManager(new Core.Configuration.AgentOptions { FleetKillGraceMs = null, GracefulShutdownTimeoutSeconds = 2 });
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        try
        {
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { Verb = "kill", SessionId = session.Id.ToString() });

            Assert.Equal(2000, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "kill", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "kill", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- patch (rename) ----------

    [Fact]
    public async Task DispatchAsync_Patch_RenamesSessionAndReturnsUpdatedDto()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand
            {
                CommandId = "p1",
                Verb = "patch",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "Renamed-Session" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("p1", result.CommandId);
            Assert.Equal("Renamed-Session", session.CustomName);

            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("Renamed-Session", dto.Name);
            Assert.Equal("dir-A", dto.DirectorId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_EmptyName_ClearsCustomName()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.CustomName = "Something"; // start with a custom name
            var command = new DirectorCommand
            {
                Verb = "patch",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "   " }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.CustomName); // whitespace-only clears to null (falls back to default)
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand
            {
                Verb = "patch",
                SessionId = Guid.NewGuid().ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "x" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "patch", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- create (heaviest verb) ----------

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path (ConPty
    // spawn, name-at-birth, wingman opt-in) without depending on an installed coding-agent CLI.
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    private static DirectorCommand CreateCommand(NewSessionRequest req) => new()
    {
        CommandId = "c1",
        Verb = "create",
        SessionId = "",
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task DispatchAsync_Create_MakesSessionWithRightAgentNameAndWingman()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "create-executor-test",
                WingmanEnabled = false,
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("RawCli", dto.Agent);
            Assert.Equal("create-executor-test", dto.Name);
            Assert.Equal("dir-A", dto.DirectorId);

            // The session actually exists in the manager with the requested wingman opt-out.
            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            var session = sm.GetSession(sid);
            Assert.NotNull(session);
            Assert.False(session.WingmanEnabled);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_MissingRepoPath_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = "", Agent = "RawCli", Command = TestShellPath });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_UnknownAgent_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "NotARealAgent" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_WeakExplicitName_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var repo = Path.GetTempPath();
            var folder = Path.GetFileName(repo.TrimEnd('\\', '/'));
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = repo,
                Agent = "RawCli",
                Command = TestShellPath,
                Name = folder, // bare repo folder name is a weak explicit name -> rejected (issue #800)
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_RawCliWithoutCommand_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "RawCli", Command = null });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-goal (increment 6: side-effecting verb via the services context) ----------

    [Fact]
    public async Task DispatchAsync_WingmanGoal_SetsGoalAndReturnsBody()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand
            {
                CommandId = "g1",
                Verb = "wingman-goal",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "ship the stream" }, Json),
            };

            // services=null -> the cache-warm side effect is skipped, but the goal is still set.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("ship the stream", session.WingmanGoal);
            Assert.NotNull(result.BodyJson);
            Assert.Contains("ship the stream", result.BodyJson);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_EmptyGoal_ClearsIt()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.SetWingmanGoal("old goal");
            var command = new DirectorCommand
            {
                Verb = "wingman-goal",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = null }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.WingmanGoal);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand
            {
                Verb = "wingman-goal",
                SessionId = Guid.NewGuid().ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "x" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "wingman-goal", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UnknownVerb_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { CommandId = "x", Verb = "no-such-verb", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }
}
