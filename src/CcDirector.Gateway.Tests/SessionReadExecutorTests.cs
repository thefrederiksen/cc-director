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
}
