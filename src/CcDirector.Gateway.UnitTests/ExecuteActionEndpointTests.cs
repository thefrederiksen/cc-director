using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;


/// <summary>
/// Gateway Cleanup mission (the cut): the Director's <c>POST /sessions/{sid}/execute-action</c> HTTP
/// route is DELETED. The DUMB execute leg of the Wingman decide/execute split (issue #327) now rides
/// the tunnel as the "execute-action" verb, whose shared core is
/// <see cref="SessionWriteExecutor.ExecuteAction"/> - it validates the id/session, deserializes the
/// caller's <see cref="WingmanAction"/>, and runs it verbatim through the single write chokepoint
/// <see cref="Core.Wingman.WingmanActionExecutor"/> with zero decision logic and no LLM.
///
/// These tests drive that core DIRECTLY against real buffer-only sessions, so the exact bytes written
/// to the PTY are asserted, not inferred, and the executor's outcome contract is pinned exactly as the
/// Gateway boundary now surfaces it:
///   - a caller/target error is a FAILED <see cref="DirectorCommandResult"/> (BadRequest / NotFound),
///     which the Gateway maps to 400 / 404;
///   - an executor OUTCOME (ok / suppressed / session_gone / bad_request) rides back INSIDE the
///     serialized <see cref="WingmanActResult"/> on a successful command, which the Gateway maps to
///     its HTTP code (session_gone -&gt; 410, bad_request -&gt; 400) - so the outcome, not the transport
///     status, is what these tests assert.
///
/// The old HTTP token-gate test is dropped: authentication is enforced once at the tunnel/Gateway
/// boundary now, not on a per-Director HTTP route.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ExecuteActionEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SessionManager _sm;

    public ExecuteActionEndpointTests()
    {
        _sm = new SessionManager(new AgentOptions());
    }

    public void Dispose() => _sm.Dispose();

    private (Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var backend = new ExecuteActionTestBackend();
        var session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (session, backend);
    }

    private static byte[] BufferBytes(ExecuteActionTestBackend backend)
    {
        if (backend.Buffer is null)
            throw new InvalidOperationException("test backend has no buffer");
        return backend.Buffer.DumpAll();
    }

    // Build + run the "execute-action" verb core exactly as the tunnel dispatch does.
    private DirectorCommandResult Exec(string sessionId, WingmanAction? action)
    {
        var payload = action is null ? "null" : JsonSerializer.Serialize(action, Web);
        return SessionWriteExecutor.ExecuteAction(_sm, new DirectorCommand
        {
            Verb = "execute-action",
            SessionId = sessionId,
            PayloadJson = payload,
        });
    }

    private static WingmanActResult Body(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status); // the executor outcome rides inside the body
        var body = JsonSerializer.Deserialize<WingmanActResult>(result.BodyJson!, Web);
        Assert.NotNull(body);
        return body!;
    }

    // ---------- The verb executes exactly what the caller passed ----------

    [Fact]
    public void ExecuteAction_Submit_WritesTextThenEnterAndEchoesActionVerbatim()
    {
        var (session, backend) = NewSession();

        var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "hello from execute-action", Reason = "caller decided" };
        var result = Body(Exec(session.Id.ToString(), action));

        Assert.True(result.Performed);
        Assert.Equal(WingmanActResult.StatusOk, result.Status);
        // Untransformed mapping: the result echoes the exact action/text/reason passed in.
        Assert.Equal(WingmanAction.ActSubmit, result.Action);
        Assert.Equal("hello from execute-action", result.Text);
        Assert.Equal("caller decided", result.Reason);
        // No LLM in the path: Model stays empty (wingman/act stamps it; this verb never does).
        Assert.Equal("", result.Model);

        // The exact bytes a human keystroke path would produce: the text, then Enter.
        var written = Encoding.UTF8.GetString(BufferBytes(backend));
        Assert.Contains("hello from execute-action", written);
        Assert.EndsWith("\r", written);

        // Executor invariant preserved: the audit trail recorded the action.
        Assert.Single(session.RecentWingmanActions);
        Assert.Equal(WingmanAction.ActSubmit, session.RecentWingmanActions[0].Action);
    }

    [Fact]
    public void ExecuteAction_SendKeys_WritesExactMappedBytes()
    {
        var (session, backend) = NewSession();

        var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
        action.Keys.AddRange(new[] { "Down", "Enter" });
        var result = Body(Exec(session.Id.ToString(), action));

        Assert.True(result.Performed);
        Assert.Equal(new[] { "Down", "Enter" }, result.Keys);

        // ESC [ B (Down) then CR (Enter) - byte-for-byte what KeyChords maps, nothing else.
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x42, 0x0D }, BufferBytes(backend));
    }

    // ---------- Executor invariants surface through the verb ----------

    [Fact]
    public void ExecuteAction_RepeatWithinCooldownOnUnchangedScreen_ReportsSuppressed()
    {
        var (session, backend) = NewSession();

        // Ctrl+C is a C0 control the terminal grid drops, so the screen hash is identical
        // across both calls - exactly the idempotency case the 3s cooldown guards.
        var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
        action.Keys.Add("Ctrl+C");

        var first = Body(Exec(session.Id.ToString(), action));
        Assert.True(first.Performed);

        var second = Body(Exec(session.Id.ToString(), action));
        Assert.False(second.Performed);
        Assert.Equal(WingmanActResult.StatusSuppressed, second.Status);

        // Exactly one Ctrl+C byte reached the PTY.
        Assert.Equal(new byte[] { 0x03 }, BufferBytes(backend));
    }

    [Fact]
    public void ExecuteAction_OnExitedSession_Returns410AndInjectsNothing()
    {
        var (session, backend) = NewSession();
        backend.RaiseProcessExited(1); // drives Session.Status -> Exited via the real backend event

        var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "must not land" };
        // The exited-session outcome (which the Gateway maps to 410) rides inside the result body.
        var result = Body(Exec(session.Id.ToString(), action));

        Assert.False(result.Performed);
        Assert.Equal(WingmanActResult.StatusSessionGone, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("nothing was injected", result.Error);

        Assert.Empty(BufferBytes(backend));
    }

    [Fact]
    public void ExecuteAction_None_IsAcceptedAsNoOp()
    {
        var (session, backend) = NewSession();

        var result = Body(Exec(session.Id.ToString(), new WingmanAction { Action = WingmanAction.ActNone }));

        Assert.False(result.Performed);
        Assert.Equal(WingmanActResult.StatusOk, result.Status);

        Assert.Empty(BufferBytes(backend));
        Assert.Empty(session.RecentWingmanActions);
    }

    // ---------- Caller errors inject nothing ----------

    [Fact]
    public void ExecuteAction_UnknownActionName_Returns400()
    {
        var (session, backend) = NewSession();

        // An unknown action name is an executor OUTCOME (bad_request rides in the body); the Gateway
        // maps that body status to 400.
        var result = Body(Exec(session.Id.ToString(), new WingmanAction { Action = "frobnicate", Text = "x" }));

        Assert.Equal(WingmanActResult.StatusBadRequest, result.Status);
        Assert.Empty(BufferBytes(backend));
    }

    [Fact]
    public void ExecuteAction_NullBody_Returns400()
    {
        var (session, _) = NewSession();

        var result = Exec(session.Id.ToString(), null);
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public void ExecuteAction_UnknownSession_Returns404()
    {
        var result = Exec(Guid.NewGuid().ToString(), new WingmanAction { Action = WingmanAction.ActNone });
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    [Fact]
    public void ExecuteAction_InvalidSessionIdFormat_Returns400()
    {
        var result = Exec("not-a-guid", new WingmanAction { Action = WingmanAction.ActNone });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }
}
