using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 10's SURVIVING HALF: the ask-and-wait verb could never observe the answer it waited for.
///
/// <c>POST /fleet/ask</c> ("ask a session and wait for its answer", the CLI's
/// <c>cc-devthrottle message ask</c>) waited for the target to reach <c>ActivityState.Idle</c> - a state
/// NOTHING has ever written. The auto-drain that Idle was invented for was deleted by #1564, whose own
/// comment records that its tests "passed for fourteen months by calling ApplyTerminalActivityState(Idle)
/// directly and injecting a state production never emits". THIS reader was left behind, still waiting on
/// the state that pull request had just finished proving dead. So the loop always ran the FULL timeout and
/// the verdict was ALWAYS "timeout".
///
/// THE TIMING IS THE SYMPTOM, which is why these tests assert the clock and not just the status. A wait
/// that returns the right answer after burning its whole timeout is still the defect: the old code would
/// eventually report a timeout for a session that had been sitting at its prompt the entire time.
///
/// A DEAD STATE IS NOT DEAD UNTIL ITS LAST READER IS GONE.
///
/// Design: docs/new_architecture/session-state.html, defect 10.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetAskSettlesTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private SessionManager _sm = null!;
    private ControlApiHost _host = null!;
    private HttpClient _client = null!;

    /// <summary>The ask's timeout. Short, because a PASS must be fast and a regression must be obvious:
    /// the old code burned exactly this long, every time.</summary>
    private const int AskTimeoutMs = 4000;

    public FleetAskSettlesTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-fleetask-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = DirectorTestClient.Admin(port);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A real session whose agent has NO transcript reader (RawCli).
    ///
    /// That detail is load-bearing for the TIMING assertions, and finding out why cost a wrong test first.
    /// After the wait, the ask polls for a NEW assistant message in the agent's transcript - up to 50 times
    /// at 500ms, so ~25 SECONDS - before falling back to a buffer scrape. For a transcript-capable agent
    /// (ClaudeCode, the default) that poll dominates the request end to end, so the elapsed time says
    /// nothing about the wait: a burned 4s clock and a prompt settle both come back at ~26s, three seconds
    /// apart. On an agent with no transcript the poll is skipped entirely, the request time IS the wait
    /// time, and the clock becomes the honest discriminator it needs to be.
    ///
    /// (The 25-second poll on a settled session that produced no new assistant message is real and costs
    /// every ask that falls back to the buffer. It is out of this defect's scope and reported, not fixed.)
    /// </summary>
    private Session NewSession()
    {
        var s = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        s.AgentKind = Core.Agents.AgentKind.RawCli;
        return s;
    }

    /// <summary>
    /// The turn SETTLES while the ask is waiting, and the ask notices - promptly.
    ///
    /// Note the shape, because a first attempt at this test got it wrong and the failure taught me why: the
    /// ask SENDS the question before it waits, and a submission drives the session to Working. So the target
    /// cannot be pre-parked at a turn end - it has to settle DURING the wait, which is exactly what a real
    /// turn does. (Left to the real detector that settle takes the full ten-second quiet threshold, so the
    /// test drives the same edge the detector drives, just without the wall-clock wait.)
    ///
    /// Before the fix this waited for Idle, which nothing writes, so the settle below changed nothing and
    /// the ask burned all four seconds and returned a 504.
    /// </summary>
    [Fact]
    public async Task WhenTheTurnSettles_TheAskNoticesPromptly_InsteadOfBurningTheWholeTimeout()
    {
        var target = NewSession();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ask = _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest
        {
            ToSessionId = target.Id.ToString(),
            Question = "are you there?",
            TimeoutMs = AskTimeoutMs,
        });

        // Let the ask deliver the question and enter its wait, then end the turn the way the sensor does.
        await Task.Delay(600);
        target.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var resp = await ask;
        sw.Stop();
        var body = await resp.Content.ReadFromJsonAsync<FleetAskResponse>();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(body!.Answered, "the turn settled inside the timeout, so the ask must be answered");
        Assert.Equal("idle", body.Status); // the WIRE word for "the turn settled" - see the relay's own mapping

        // THE SYMPTOM. The old code observed nothing and ran the clock out every single time. Anything close
        // to the full timeout means the wait is once again watching for something that never happens.
        Assert.True(sw.ElapsedMilliseconds < AskTimeoutMs,
            $"the ask took {sw.ElapsedMilliseconds}ms of a {AskTimeoutMs}ms timeout - it is burning the clock, " +
            "which is defect 10: the wait is gated on a state production never emits");
    }

    /// <summary>
    /// A target that EXITS before answering is reported as a failure, not as an answer. This outcome was
    /// unreachable before the fix (the wait could only ever run out the clock), so nothing had to consider
    /// it - and widening the predicate WITHOUT this arm would have returned Answered=true carrying whatever
    /// the buffer scrape caught. That is the same defect wearing a new state's name.
    /// </summary>
    [Fact]
    public async Task WhenTheTargetExitsMidAnswer_TheAskReportsFailed_NotAnAnswer()
    {
        var target = NewSession();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ask = _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest
        {
            ToSessionId = target.Id.ToString(),
            Question = "are you there?",
            TimeoutMs = AskTimeoutMs,
        });

        await Task.Delay(600);
        target.ApplyTerminalActivityState(ActivityState.Exited);

        var resp = await ask;
        sw.Stop();
        var body = await resp.Content.ReadFromJsonAsync<FleetAskResponse>();

        Assert.False(body!.Answered, "a dead session did not answer, and must never be reported as if it had");
        Assert.Equal("failed", body.Status); // matches the Gateway relay's own mapping for Exited/Failed
        Assert.True(sw.ElapsedMilliseconds < AskTimeoutMs,
            $"the ask took {sw.ElapsedMilliseconds}ms - a dead target must end the wait, not run the clock out");
    }
}
