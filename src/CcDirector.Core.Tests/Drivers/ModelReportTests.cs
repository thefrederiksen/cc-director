using CcDirector.Core.Agents;
using CcDirector.Core.Codex;
using CcDirector.Core.Copilot;
using CcDirector.Core.Drivers;
using CcDirector.Core.Grok;
using CcDirector.Core.OpenCode;
using CcDirector.Core.Pi;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

// =====================================================================================
// ModelReport capability (issue #1637): every driver that can answer "what model is the
// tool currently using" answers it from the tool's own records; a driver that cannot is
// honestly absent and throws. Fixture lines are copied from the REAL files verified on
// this machine on 2026-07-15 (claude 2.1.210, codex 0.143.0, pi 0.79.4, copilot 1.0.70,
// grok 0.2.93, opencode 1.15.12).
// =====================================================================================
public sealed class ModelReportTests
{
    // ---- Capability declarations ----

    [Fact]
    public void Capabilities_DriversWithVerifiedModelStores_DeclareModelReport()
    {
        Assert.True(AgentDrivers.For(AgentKind.ClaudeCode).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.True(AgentDrivers.For(AgentKind.Codex).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.True(AgentDrivers.For(AgentKind.Pi).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.True(AgentDrivers.For(AgentKind.Copilot).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.True(AgentDrivers.For(AgentKind.Grok).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.True(AgentDrivers.For(AgentKind.OpenCode).Capabilities.HasFlag(DriverCapabilities.ModelReport));
    }

    [Fact]
    public void Capabilities_DriversWithoutVerifiedModelStores_DoNotDeclareModelReport()
    {
        // Gemini 0.1.11 records no model anywhere readable; Cursor is unverifiable here.
        Assert.False(AgentDrivers.For(AgentKind.Gemini).Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.False(AgentDrivers.For(AgentKind.Cursor).Capabilities.HasFlag(DriverCapabilities.ModelReport));
    }

    [Fact]
    public void ReadCurrentModel_UndeclaredDriver_ThrowsNotSupported()
    {
        var gemini = new GenericDriver(AgentKind.Gemini);
        Assert.Throws<NotSupportedException>(() => gemini.ReadCurrentModel("sid", @"C:\repo", null));

        // CursorDriver inherits the interface default, which is the honest throw.
        IAgentDriver cursor = new CursorDriver();
        Assert.Throws<NotSupportedException>(() => cursor.ReadCurrentModel("sid", @"C:\repo", null));
    }

    [Fact]
    public void ReadCurrentModel_GenericDriverWithReader_DeclaresAndDelegates()
    {
        string? seenDirectory = null;
        var driver = new GenericDriver(AgentKind.Grok, currentModelReader: dir =>
        {
            seenDirectory = dir;
            return "grok-4.5";
        });

        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.ModelReport));
        Assert.Equal("grok-4.5", driver.ReadCurrentModel("sid", @"C:\repo", null));
        Assert.Equal(@"C:\repo", seenDirectory);
    }

    // ---- ClaudeDriver: transcript model wins; launch args answer before the first turn ----

    private static ClaudeDriver DriverReturning(SessionUsageDto? usage)
        => new(new StubReader(usage));

    [Fact]
    public void ReadCurrentModel_Claude_TranscriptModelWins()
    {
        // The transcript reflects a mid-session /model switch; the launch flag is stale.
        var usage = new SessionUsageDto { ContextModel = "claude-fable-5", AssistantMessageCount = 3 };
        var model = DriverReturning(usage).ReadCurrentModel("sid", @"C:\repo", "--model opus");
        Assert.Equal("claude-fable-5", model);
    }

    [Fact]
    public void ReadCurrentModel_Claude_NoTurnYet_LaunchArgsAnswer()
    {
        var usage = new SessionUsageDto { AssistantMessageCount = 0 };
        Assert.Equal("opus[1m]",
            DriverReturning(usage).ReadCurrentModel("sid", @"C:\repo", "--dangerously-skip-permissions --model opus[1m]"));
        Assert.Equal("sonnet",
            DriverReturning(null).ReadCurrentModel("sid", @"C:\repo", "--model=sonnet"));
    }

    [Fact]
    public void ReadCurrentModel_Claude_NoTranscriptNoLaunchModel_ReturnsNull()
    {
        Assert.Null(DriverReturning(null).ReadCurrentModel("sid", @"C:\repo", "--dangerously-skip-permissions"));
        Assert.Null(DriverReturning(null).ReadCurrentModel("sid", @"C:\repo", null));
    }

    // ---- CodexCurrentModel: the LAST turn_context model wins ----

    [Fact]
    public void CodexCompute_RealTurnContextLine_ReturnsModel()
    {
        // Shape copied from a real codex-cli 0.143.0 rollout on this machine.
        var lines = new[]
        {
            """{"timestamp":"2026-07-15T14:26:52.248Z","type":"session_meta","payload":{"session_id":"019f662c-4847-7822-b959-c12c7346498c","cwd":"D:\\repo","model_provider":"openai"}}""",
            """{"timestamp":"2026-07-15T14:26:52.274Z","type":"turn_context","payload":{"turn_id":"t1","cwd":"D:\\repo","model":"gpt-5.5","comp_hash":"2911"}}""",
        };
        Assert.Equal("gpt-5.5", CodexCurrentModel.Compute(lines));
    }

    [Fact]
    public void CodexCompute_ModelSwitchMidSession_LastTurnContextWins()
    {
        var lines = new[]
        {
            """{"type":"turn_context","payload":{"model":"gpt-5.5"}}""",
            """{"type":"turn_context","payload":{"model":"gpt-5.5-codex"}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100}}}}""",
        };
        Assert.Equal("gpt-5.5-codex", CodexCurrentModel.Compute(lines));
    }

    [Fact]
    public void CodexCompute_NoTurnContextOrTornTail_ReturnsNullOrSkips()
    {
        Assert.Null(CodexCurrentModel.Compute(new[]
        {
            """{"type":"session_meta","payload":{"cwd":"D:\\repo"}}""",
        }));

        // A torn tail line (codex mid-write) is skipped, earlier model still wins.
        Assert.Equal("gpt-5.5", CodexCurrentModel.Compute(new[]
        {
            """{"type":"turn_context","payload":{"model":"gpt-5.5"}}""",
            """{"type":"turn_context","payl""",
        }));
    }

    // ---- PiCurrentModel: the LAST assistant message model wins ----

    [Fact]
    public void PiCompute_RealAssistantLine_ReturnsModel()
    {
        // Shape copied from a real pi 0.79.4 session file on this machine.
        var lines = new[]
        {
            """{"type":"session","cwd":"D:\\repo"}""",
            """{"type":"message","message":{"role":"user","content":"hello"}}""",
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.5","usage":{"input":3838,"output":33}}}""",
        };
        Assert.Equal("gpt-5.5", PiCurrentModel.Compute(lines));
    }

    [Fact]
    public void PiCompute_ModelSwitch_LastAssistantWins_UserLinesIgnored()
    {
        var lines = new[]
        {
            """{"type":"message","message":{"role":"assistant","model":"gpt-5.5"}}""",
            """{"type":"message","message":{"role":"assistant","model":"claude-fable-5"}}""",
            """{"type":"message","message":{"role":"user","content":"thanks"}}""",
        };
        Assert.Equal("claude-fable-5", PiCurrentModel.Compute(lines));
    }

    [Fact]
    public void PiCompute_NoAssistantYet_ReturnsNull()
    {
        Assert.Null(PiCurrentModel.Compute(new[]
        {
            """{"type":"session","cwd":"D:\\repo"}""",
            """{"type":"message","message":{"role":"user","content":"hello"}}""",
        }));
    }

    // ---- CopilotCurrentModel: the LAST model-bearing event wins ----

    [Fact]
    public void CopilotCompute_RealTurnStartEvent_ReturnsModel()
    {
        // Shape copied from a real copilot 1.0.70 events.jsonl on this machine.
        var lines = new[]
        {
            """{"type":"session.start","data":{"sessionId":"a6448823"},"id":"e1","timestamp":"2026-07-10T15:21:06.578Z"}""",
            """{"type":"assistant.turn_start","data":{"turnId":"0","model":"claude-haiku-4.5","interactionId":"0fdef53a"},"id":"e2","timestamp":"2026-07-10T15:21:11.344Z"}""",
        };
        Assert.Equal("claude-haiku-4.5", CopilotCurrentModel.Compute(lines));
    }

    [Fact]
    public void CopilotCompute_ModelSwitch_LastEventWins()
    {
        var lines = new[]
        {
            """{"type":"assistant.turn_start","data":{"model":"claude-haiku-4.5"}}""",
            """{"type":"assistant.message","data":{"model":"gpt-5.5","content":""}}""",
            """{"type":"user.message","data":{"content":"hi"}}""",
        };
        Assert.Equal("gpt-5.5", CopilotCurrentModel.Compute(lines));
    }

    [Fact]
    public void CopilotCompute_NoModelEventYet_ReturnsNull()
    {
        Assert.Null(CopilotCurrentModel.Compute(new[]
        {
            """{"type":"session.start","data":{"sessionId":"a6448823"}}""",
        }));
    }

    [Fact]
    public void CopilotReadForSession_BlankOrMissingSession_ReturnsNull()
    {
        Assert.Null(CopilotCurrentModel.ReadForSession(""));
        Assert.Null(CopilotCurrentModel.ReadForSession(Guid.NewGuid().ToString()));
    }

    // ---- GrokCurrentModel: the LAST assistant model_id wins ----

    [Fact]
    public void GrokCompute_RealAssistantLine_ReturnsModelId()
    {
        // Shape copied from a real grok 0.2.93 chat_history.jsonl on this machine.
        var lines = new[]
        {
            """{"type":"user","content":"hello"}""",
            """{"type":"assistant","content":"hi","model_id":"grok-4.5","model_fingerprint":"fp_a39489019fa99b6e","reasoning_effort":"high"}""",
        };
        Assert.Equal("grok-4.5", GrokCurrentModel.Compute(lines));
    }

    [Fact]
    public void GrokCompute_ModelSwitch_LastAssistantWins_NonAssistantIgnored()
    {
        var lines = new[]
        {
            """{"type":"assistant","model_id":"grok-4"}""",
            """{"type":"assistant","model_id":"grok-4.5"}""",
            """{"type":"user","content":"thanks"}""",
        };
        Assert.Equal("grok-4.5", GrokCurrentModel.Compute(lines));
    }

    [Fact]
    public void GrokCompute_NoAssistantYet_ReturnsNull()
    {
        Assert.Null(GrokCurrentModel.Compute(new[] { """{"type":"user","content":"hello"}""" }));
    }

    // ---- OpenCodeCurrentModel: the newest matching session row's model JSON ----

    [Fact]
    public void OpenCodeParseModelId_RealBlob_ReturnsId()
    {
        // Blob copied from a real opencode 1.15.12 session row on this machine.
        Assert.Equal("gpt-5.3-chat-latest",
            OpenCodeCurrentModel.ParseModelId("""{"id":"gpt-5.3-chat-latest","providerID":"openai"}"""));
        Assert.Null(OpenCodeCurrentModel.ParseModelId(null));
        Assert.Null(OpenCodeCurrentModel.ParseModelId(""));
        Assert.Null(OpenCodeCurrentModel.ParseModelId("not json"));
        Assert.Null(OpenCodeCurrentModel.ParseModelId("""{"providerID":"openai"}"""));
    }

    [Fact]
    public void OpenCodeReadFrom_NewestMatchingSessionWins_OtherDirectoriesIgnored()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencode-model-fixture-" + Guid.NewGuid().ToString("N") + ".db");
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                Execute(conn,
                    """
                    CREATE TABLE session (
                        id TEXT PRIMARY KEY,
                        directory TEXT NOT NULL,
                        model TEXT,
                        time_updated INTEGER
                    );
                    """);
                InsertSession(conn, "ses-old", repo, """{"id":"gpt-5.3-chat-latest","providerID":"openai"}""", 1000);
                InsertSession(conn, "ses-new", repo + "\\", """{"id":"claude-fable-5","providerID":"anthropic"}""", 2000);
                InsertSession(conn, "ses-other", @"C:\other\repo", """{"id":"gpt-4o","providerID":"openai"}""", 3000);
            }

            Assert.Equal("claude-fable-5", OpenCodeCurrentModel.ReadFrom(repo, dbPath));
            Assert.Null(OpenCodeCurrentModel.ReadFrom(@"C:\no\such\repo", dbPath));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void OpenCodeReadFrom_MissingStore_ReturnsNull()
    {
        Assert.Null(OpenCodeCurrentModel.ReadFrom(@"C:\repo", Path.Combine(Path.GetTempPath(), "no-such.db")));
    }

    // ---- helpers ----

    private static SqliteConnection OpenWritable(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    private static void InsertSession(SqliteConnection conn, string id, string directory, string modelJson, long timeUpdated)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO session (id, directory, model, time_updated) VALUES (@id, @dir, @model, @updated)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@dir", directory);
        cmd.Parameters.AddWithValue("@model", modelJson);
        cmd.Parameters.AddWithValue("@updated", timeUpdated);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class StubReader : ITranscriptReader
    {
        private readonly SessionUsageDto? _usage;
        public StubReader(SessionUsageDto? usage) => _usage = usage;
        public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => new();
        public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => _usage;
        public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();
        public DateTime? LastCompactionUtc(string claudeSessionId, string repoPath) => null;
    }
}
