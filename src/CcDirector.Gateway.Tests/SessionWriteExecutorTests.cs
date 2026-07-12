using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker W1): parity tests for the Director session STATE-WRITE verbs
/// moved onto the shared tunnel dispatch (<see cref="SessionCommandExecutor.DispatchAsync"/> routing into
/// <see cref="SessionWriteExecutor"/>). These verbs used to live only as REST lambdas; now the REST path and
/// the Gateway stream down-channel call the SAME core, so this asserts the core's guards and effect directly
/// against a real <see cref="SessionManager"/> holding an embedded buffer-only session (the same harness the
/// resize/prompt exemplars use). Representative coverage per the mission brief: an invalid id -&gt;
/// BadRequest, a missing session -&gt; NotFound, and a real success effect.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionWriteExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand Command(string verb, string sessionId, object? payload = null) => new()
    {
        CommandId = "cmd-w1",
        Verb = verb,
        SessionId = sessionId,
        PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Json),
    };

    // ---------- mobile-mode ----------

    [Fact]
    public async Task DispatchAsync_MobileMode_Enabled_SetsTextViewMode()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("mobile-mode", session.Id.ToString(), new MobileModeRequest(true)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("cmd-w1", result.CommandId);
            Assert.Equal(MobileViewMode.Text, session.ViewMode);
            Assert.True(session.MobileMode);
            Assert.Contains("mobileMode", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_MobileMode_Disabled_SetsViewModeOff()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.ViewMode = MobileViewMode.Text; // start watching
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("mobile-mode", session.Id.ToString(), new MobileModeRequest(false)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(MobileViewMode.Off, session.ViewMode);
            Assert.False(session.MobileMode);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_MobileMode_EmptyPayload_DefaultsToEnabled()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("mobile-mode", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(MobileViewMode.Text, session.ViewMode); // absent body -> default enable
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_MobileMode_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("mobile-mode", "not-a-guid", new MobileModeRequest(true)));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_MobileMode_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("mobile-mode", Guid.NewGuid().ToString(), new MobileModeRequest(true)));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- voice-mode ----------

    [Fact]
    public async Task DispatchAsync_VoiceMode_Enabled_SetsVoiceViewMode()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("voice-mode", session.Id.ToString(), new VoiceModeRequest(true)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(MobileViewMode.Voice, session.ViewMode);
            Assert.True(session.VoiceMode);
            Assert.Contains("voiceMode", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_VoiceMode_Disabled_FallsBackToTextView()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.ViewMode = MobileViewMode.Voice;
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("voice-mode", session.Id.ToString(), new VoiceModeRequest(false)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(MobileViewMode.Text, session.ViewMode);
            Assert.False(session.VoiceMode);
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-enabled ----------

    [Fact]
    public async Task DispatchAsync_WingmanEnabled_False_TurnsOffAndClearsExplaining()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.IsExplaining = true; // a yellow dot in flight
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-enabled", session.Id.ToString(), new WingmanEnabledRequest(false)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.False(session.WingmanEnabled);
            Assert.False(session.IsExplaining); // cleared so the dot doesn't stick
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanEnabled_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-enabled", Guid.NewGuid().ToString(), new WingmanEnabledRequest(true)));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- relink ----------

    [Fact]
    public async Task DispatchAsync_Relink_RepointsClaudeSessionId()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("relink", session.Id.ToString(), new RelinkRequest { ClaudeSessionId = "claude-xyz" }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("claude-xyz", session.ClaudeSessionId);
            Assert.Contains("claude-xyz", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Relink_BlankClaudeSessionId_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("relink", session.Id.ToString(), new RelinkRequest { ClaudeSessionId = "   " }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Relink_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("relink", Guid.NewGuid().ToString(), new RelinkRequest { ClaudeSessionId = "abc" }));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- request-deletion / cancel-deletion ----------

    [Fact]
    public async Task DispatchAsync_RequestDeletion_FlagsSessionWithReason()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("request-deletion", session.Id.ToString(), new SessionDeletionRequest { Reason = "jobs-auto: nothing to report" }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.True(session.PendingDeletion);
            Assert.Equal("jobs-auto: nothing to report", session.DeletionReason);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_CancelDeletion_ClearsPendingDeletion()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.MarkForDeletion("changed my mind soon");
            Assert.True(session.PendingDeletion);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("cancel-deletion", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.False(session.PendingDeletion);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_RequestDeletion_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("request-deletion", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- history-picker ----------

    [Fact]
    public async Task DispatchAsync_HistoryPicker_ClaudeSession_ReturnsOk()
    {
        var (sm, session, _) = NewSession(); // default AgentKind is ClaudeCode, whose driver opens the picker
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("history-picker", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Contains("accepted", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HistoryPicker_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("history-picker", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- clear-context (guards only; the success path needs a live transcript-capable driver) ----------

    [Fact]
    public async Task DispatchAsync_ClearContext_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("clear-context", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ClearContext_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("clear-context", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- execute-action (the mechanical write chokepoint) ----------

    [Fact]
    public async Task DispatchAsync_ExecuteAction_Submit_WritesTextThenEnter()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "over-the-tunnel", Reason = "caller decided" };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("execute-action", session.Id.ToString(), action));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var actResult = JsonSerializer.Deserialize<WingmanActResult>(result.BodyJson ?? "", Json);
            Assert.NotNull(actResult);
            Assert.True(actResult.Performed);
            Assert.Equal(WingmanActResult.StatusOk, actResult.Status);
            Assert.Equal("over-the-tunnel", actResult.Text);

            Assert.NotNull(backend.Buffer);
            var written = Encoding.UTF8.GetString(backend.Buffer.DumpAll());
            Assert.Contains("over-the-tunnel", written);
            Assert.EndsWith("\r", written); // text, then Enter
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ExecuteAction_ExitedSession_ReportsSessionGoneAndInjectsNothing()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            backend.RaiseProcessExited(1); // drive Session.Status -> Exited via the real backend event
            Assert.True(session.Status is SessionStatus.Exited or SessionStatus.Failed);

            var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "must not land" };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("execute-action", session.Id.ToString(), action));

            // The executor outcome rides back inside the WingmanActResult (the REST layer maps it to 410).
            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var actResult = JsonSerializer.Deserialize<WingmanActResult>(result.BodyJson ?? "", Json);
            Assert.NotNull(actResult);
            Assert.False(actResult.Performed);
            Assert.Equal(WingmanActResult.StatusSessionGone, actResult.Status);
            Assert.NotNull(actResult.Error);
            Assert.Contains("nothing was injected", actResult.Error);

            Assert.NotNull(backend.Buffer);
            Assert.Empty(backend.Buffer.DumpAll());
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ExecuteAction_NullBody_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("execute-action", session.Id.ToString())); // empty payload -> null action

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ExecuteAction_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("execute-action", Guid.NewGuid().ToString(), new WingmanAction { Action = WingmanAction.ActNone }));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- handover-generate (Gateway Cleanup Phase 0 wave 3: director-level, no target session id) ----------

    [Fact]
    public async Task DispatchAsync_HandoverGenerate_MissingFromSessionId_ReturnsBadRequest()
    {
        // An empty payload deserializes to null -> the fromSessionId guard fires (the route's 400).
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("handover-generate", ""));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoverGenerate_BothTargets_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var req = new HandoverRequest
            {
                FromSessionId = Guid.NewGuid().ToString(),
                ToSessionId = Guid.NewGuid().ToString(),
                ToRepoPath = Path.GetTempPath(),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("handover-generate", "", req));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoverGenerate_MissingSource_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var req = new HandoverRequest
            {
                FromSessionId = Guid.NewGuid().ToString(),
                ToSessionId = Guid.NewGuid().ToString(),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("handover-generate", "", req));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoverGenerate_ExistingSourceAndTarget_ReturnsOkWithTargetSession()
    {
        // Source and target are both embedded sessions on this manager. Neither has a Claude session id, so the
        // context is built from the simple branch (no file IO); the target receives it via SendTextAsync. The
        // core returns the target mapped through the plain Map, exactly as the create verb does.
        var (sm, source, _) = NewSession();
        try
        {
            var target = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
            var req = new HandoverRequest
            {
                FromSessionId = source.Id.ToString(),
                ToSessionId = target.Id.ToString(),
                ArchiveToVault = false,
            };
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("handover-generate", "", req));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("cmd-w1", result.CommandId);
            var resp = JsonSerializer.Deserialize<HandoverResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.True(resp!.Accepted);
            Assert.NotNull(resp.TargetSession);
            Assert.Equal(target.Id.ToString(), resp.TargetSession!.SessionId);
            Assert.False(string.IsNullOrEmpty(resp.ContextSent));
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-ask (Gateway Cleanup Phase 0 wave 3: static WingmanService, no new dependency) ----------

    [Fact]
    public async Task DispatchAsync_WingmanAsk_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-ask", "not-a-guid", new WingmanAskRequest { Question = "hi" }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanAsk_EmptyQuestion_ReturnsOkWithBadRequestOutcome()
    {
        // The question-required guard is the wingman's own bad_request OUTCOME (a 200 carrying the result), not
        // an id/session error - the REST route maps that Status to its original 400.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-ask", session.Id.ToString(), new WingmanAskRequest { Question = "" }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var ask = JsonSerializer.Deserialize<WingmanAskResult>(result.BodyJson ?? "", Json);
            Assert.NotNull(ask);
            Assert.Equal("bad_request", ask!.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanAsk_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-ask", Guid.NewGuid().ToString(), new WingmanAskRequest { Question = "hi" }));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanAsk_NoClaudeConfigured_ReturnsOkWithNoClaudeStatus()
    {
        // With an empty ClaudePath, AnswerViaSessionAsync returns Status="no_claude" without spawning a
        // process (the fail-open contract), so the free-text ask wire path is verifiable without a CLI.
        var sm = new SessionManager(new Core.Configuration.AgentOptions { ClaudePath = "" });
        try
        {
            var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("wingman-ask", session.Id.ToString(), new WingmanAskRequest { Question = "what is going on" }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var ask = JsonSerializer.Deserialize<WingmanAskResult>(result.BodyJson ?? "", Json);
            Assert.NotNull(ask);
            Assert.Equal("no_claude", ask!.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- recap-generate (Gateway Cleanup Phase 0 wave 3: static RecapGenerator, no new dependency) ----------

    [Fact]
    public async Task DispatchAsync_RecapGenerate_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("recap-generate", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_RecapGenerate_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("recap-generate", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_RecapGenerate_NoClaudeSessionId_ReturnsOkWithNoSessionIdStatus()
    {
        // A fresh embedded session has no linked Claude session id, so the core short-circuits to the domain
        // state the route returned as a 200 body (Status="no_session_id") - no generation runs, no process
        // spawns. The resolved model is stamped exactly as the route did.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("recap-generate", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<RecapResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal("no_session_id", resp!.Status);
            Assert.False(string.IsNullOrEmpty(resp.Model));
        }
        finally { sm.Dispose(); }
    }
}
