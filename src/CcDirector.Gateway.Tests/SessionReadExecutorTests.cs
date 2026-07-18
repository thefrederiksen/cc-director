using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker R1): parity tests for the session READ verbs moved onto the
/// tunnel command surface (<see cref="SessionReadExecutor"/>). Each verb is exercised through the real
/// <see cref="SessionCommandExecutor.DispatchAsync"/> path (verb map -&gt; area -&gt; core), the same way the
/// re-pointed REST route and the Gateway stream down-channel reach it, so the core is asserted exactly once
/// for both callers. Covers, for a representative spread of the verbs, the three standard outcomes an invalid
/// id (BadRequest), a missing session (NotFound), and a real success body plus the two non-standard results
/// the sources carried (a repo with no GitHub origin -&gt; Conflict; a not-yet-linked session -&gt; a 200 with
/// a domain-state status string). Reuses the buffer-only embedded-session harness from the sibling
/// SessionCommandExecutor tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionReadExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand Cmd(string verb, string sid) => new() { CommandId = "r1", Verb = verb, SessionId = sid };

    // ---------- snapshot ----------

    [Fact]
    public async Task DispatchAsync_Snapshot_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("snapshot", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Snapshot_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("snapshot", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Snapshot_ExistingSession_ReturnsMappedSessionDto()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("snapshot", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("r1", result.CommandId);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(session.Id.ToString(), dto.SessionId);
            Assert.Equal("dir-A", dto.DirectorId); // plain map stamps the director id (Gateway stamps identity later)
        }
        finally { sm.Dispose(); }
    }

    // ---------- buffer ----------

    [Fact]
    public async Task DispatchAsync_Buffer_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Buffer_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Buffer_ExistingSession_ReturnsBufferResponse()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<BufferResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(session.Id.ToString(), resp.SessionId);
        }
        finally { sm.Dispose(); }
    }

    // ---------- buffer-html ----------

    [Fact]
    public async Task DispatchAsync_BufferHtml_ExistingSession_ReturnsHtmlResponse()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer-html", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<BufferHtmlResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(session.Id.ToString(), resp.SessionId);
            // Back-compat html is the scrollback and grid concatenated.
            Assert.Equal(resp.ScrollbackHtml + resp.GridHtml, resp.Html);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_BufferHtml_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer-html", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- screen-grid (issue #1777) ----------

    [Fact]
    public async Task DispatchAsync_ScreenGrid_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("screen-grid", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ScreenGrid_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("screen-grid", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ScreenGrid_AlternateScreenMenu_ReadsTheLiveGrid_WhileScrollbackMissesIt()
    {
        // The REAL Director-layer bug (issue #1777, finding 7): drive a session ONTO the alternate screen and
        // draw a menu the way a production TUI does - absolute cursor positioning, no line feeds - whose text
        // does not contain a stock fingerprint phrase. The screen-grid verb reads the ACTIVE (alternate) grid,
        // so it returns the exact menu rows, the cursor cell parked on the selected option, and
        // IsAlternateScreen=true. The scrollback (the buffer verb) keeps only the raw byte stream: with no line
        // feeds it has no line structure, so the OLD line-based gate MISSES the menu on it - which is exactly
        // why the detector had to move to the live grid.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ScreenGridBufferBackend();
        try
        {
            var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            session.Resize(50, 14);

            // Some ordinary primary-screen scrollback first, then enter the alternate screen and paint a menu
            // with absolute positioning (\x1b[row;colH) and NO \r\n - the way a full-screen picker repaints.
            backend.Buffer!.Write(System.Text.Encoding.UTF8.GetBytes("normal shell output before the app started\r\n"));
            var draw = new System.Text.StringBuilder();
            draw.Append("\x1b[?1049h");                                  // enter the alternate screen
            draw.Append("\x1b[2J");                                       // clear
            draw.Append("\x1b[1;1HPick an environment to deploy");
            draw.Append("\x1b[2;1H > 1. staging");
            draw.Append("\x1b[3;1H   2. production");
            draw.Append("\x1b[4;1H   3. cancel");
            draw.Append("\x1b[2;3H");                                     // park the cursor on the selected option
            backend.Buffer!.Write(System.Text.Encoding.UTF8.GetBytes(draw.ToString()));

            var gridResult = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("screen-grid", session.Id.ToString()));
            Assert.Equal(DirectorCommandStatus.Ok, gridResult.Status);
            var grid = JsonSerializer.Deserialize<ScreenGridResponse>(gridResult.BodyJson ?? "", Json);
            Assert.NotNull(grid);

            // Alternate-screen correct: the ACTIVE grid holds the menu (not the frozen pre-alt primary content).
            Assert.True(grid!.HasGrid);
            Assert.True(grid.IsAlternateScreen);
            Assert.Equal("Pick an environment to deploy", grid.Rows[0]);
            Assert.Equal(" > 1. staging", grid.Rows[1]);
            Assert.Equal("   2. production", grid.Rows[2]);
            Assert.Equal("   3. cancel", grid.Rows[3]);
            // The cursor is parked on the selected option row (row index 1, "\x1b[2;3H" -> 0-based (1,2)).
            Assert.Equal(1, grid.CursorRow);
            Assert.Equal(2, grid.CursorCol);

            // The OPERATIVE bug predicate at the Director layer: the live grid IS a menu, but the scrollback
            // (buffer verb) - the old source - does NOT look like one, so detecting off it would have missed
            // the menu and typed the spoken words in. (The buffer is not literally empty - it keeps the raw
            // bytes - but with no line feeds the line-based gate finds no menu on it.)
            var bufResult = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("buffer", session.Id.ToString()));
            var buf = JsonSerializer.Deserialize<BufferResponse>(bufResult.BodyJson ?? "", Json);
            Assert.False(CcDirector.Gateway.Wingman.WingmanMenuLogic.LooksLikeMenu(buf!.Text));
            Assert.True(CcDirector.Gateway.Wingman.WingmanMenuLogic.LiveScreenLooksLikeMenu(grid.Rows));
        }
        finally { sm.Dispose(); }
    }

    /// <summary>A minimal backend with a real terminal buffer, so a session's server-side parser is created
    /// and fed via the buffer's OnBytesWritten event - the same path the real backend uses.</summary>
    private sealed class ScreenGridBufferBackend : Core.Backends.ISessionBackend
    {
        public Core.Memory.CircularTerminalBuffer? Buffer { get; } = new Core.Memory.CircularTerminalBuffer(256 * 1024);
        public int ProcessId => 1234;
        public string Status => "Buffered";
        public bool IsRunning => true;
        public bool HasExited => false;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() => Buffer?.Dispose();
    }

    // ---------- summary ----------

    [Fact]
    public async Task DispatchAsync_Summary_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("summary", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Summary_NotYetLinked_ReturnsOkWithNoSessionIdStatus()
    {
        // A fresh ClaudeCode session has no Claude session id yet: the source route returned this as a 200
        // with status "no_session_id" (a domain state), so the tunnel verb returns Ok with that body.
        var (sm, session, _) = NewSession();
        try
        {
            Assert.True(string.IsNullOrEmpty(session.ClaudeSessionId));
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("summary", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionSummaryDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("no_session_id", dto.Status);
            Assert.Equal(session.Id.ToString(), dto.SessionId);
            Assert.Equal("dir-A", dto.DirectorId);
        }
        finally { sm.Dispose(); }
    }

    // ---------- recap ----------

    [Fact]
    public async Task DispatchAsync_Recap_NotCached_ReturnsOkWithNotCachedStatus()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("recap", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<RecapResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal("not_cached", resp.Status);
            Assert.Equal(session.Id.ToString(), resp.SessionId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Recap_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("recap", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- turn-summaries ----------

    [Fact]
    public async Task DispatchAsync_TurnSummaries_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("turn-summaries", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_TurnSummaries_NoCache_ReturnsOkWithEmptyList()
    {
        // No services supplied -> no TurnSummaryCache, so the list is empty (exactly the route's null-safe
        // behaviour), still a 200.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("turn-summaries", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<TurnSummariesResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(session.Id.ToString(), resp.SessionId);
            Assert.Empty(resp.Summaries);
        }
        finally { sm.Dispose(); }
    }

    // ---------- usage ----------

    [Fact]
    public async Task DispatchAsync_Usage_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("usage", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Usage_NoClaudeSessionId_ReturnsNotFound()
    {
        // A fresh session has no linked Claude session id, so usage cannot be computed - the source route's
        // 404 for that case is preserved.
        var (sm, session, _) = NewSession();
        try
        {
            Assert.True(string.IsNullOrEmpty(session.ClaudeSessionId));
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("usage", session.Id.ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- context ----------

    [Fact]
    public async Task DispatchAsync_Context_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("context", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Context_FreshSession_ReturnsNotFound()
    {
        // A fresh session reports no context usage yet (either the driver lacks the capability or there is no
        // completed turn) - both are the source route's 404.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("context", session.Id.ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- history ----------

    [Fact]
    public async Task DispatchAsync_History_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("history", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_History_ExistingSession_ReturnsOkWithHistoryDto()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("history", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionHistoryDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(session.Id.ToString(), dto.SessionId);
        }
        finally { sm.Dispose(); }
    }

    // ---------- github-urls ----------

    [Fact]
    public async Task DispatchAsync_GithubUrls_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("github-urls", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GithubUrls_RepoWithoutGithubOrigin_ReturnsConflict()
    {
        // The temp path is not a git repo with a GitHub origin, so BuildNewIssueUrl throws
        // InvalidOperationException - the source route's 409, surfaced as a Conflict result.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("github-urls", session.Id.ToString()));
            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-view ----------

    [Fact]
    public async Task DispatchAsync_WingmanView_ExistingSession_ReturnsViewDto()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("wingman-view", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<WingmanViewDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(session.Id.ToString(), dto.SessionId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanView_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("wingman-view", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-explain ----------

    [Fact]
    public async Task DispatchAsync_WingmanExplain_ExistingSession_ReturnsExplainResponse()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("wingman-explain", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<WingmanExplainResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.False(resp.MobileMode); // a fresh session's view mode is Off
            Assert.Null(resp.Text);        // nothing cached yet
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanExplain_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("wingman-explain", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- handover (Gateway Cleanup Phase 0 wave 3: needs the Director version via SessionCommandServices) ----------

    [Fact]
    public async Task DispatchAsync_Handover_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handover", "not-a-guid"),
                new SessionCommandServices { DirectorVersion = "9.9.9-test" });
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Handover_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handover", Guid.NewGuid().ToString()),
                new SessionCommandServices { DirectorVersion = "9.9.9-test" });
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Handover_ExistingSession_ReturnsInfoBlockWithVersionFromServices()
    {
        // The identity/locate block is a pure read of the live session record. The Director version rides in
        // the services - the one dependency the tunnel command surface did not carry - and is stamped exactly
        // as the REST route stamped ControlApiHost._version. The block never carries a Director address.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-handover", Cmd("handover", session.Id.ToString()),
                new SessionCommandServices { DirectorVersion = "9.9.9-test" });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<HandoverInfoDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(session.Id.ToString(), dto!.SessionId);
            Assert.Equal("dir-handover", dto.DirectorId);
            Assert.Equal("9.9.9-test", dto.Version);
            Assert.Equal(Environment.MachineName, dto.MachineName);
            Assert.False(string.IsNullOrWhiteSpace(dto.DisplayName));
            Assert.DoesNotContain("http://", result.BodyJson ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally { sm.Dispose(); }
    }

    // ---------- handover-context (Gateway Cleanup mission: cross-Director handover reads this over the tunnel) ----------

    [Fact]
    public async Task DispatchAsync_HandoverContext_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handover-context", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoverContext_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handover-context", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoverContext_ExistingSession_ReturnsTheHandoverPromptText()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand
            {
                CommandId = "r1",
                Verb = "handover-context",
                SessionId = session.Id.ToString(),
                PayloadJson = SessionCommandExecutor.Serialize(new HandoverContextRequest { ExtraContext = "carry this note" }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var body = JsonSerializer.Deserialize<HandoverContextResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(body);
            Assert.False(string.IsNullOrWhiteSpace(body!.Text)); // the formatted handover prompt
            Assert.Contains("carry this note", body.Text);       // the extra context rode the payload into the prompt
        }
        finally { sm.Dispose(); }
    }
}
