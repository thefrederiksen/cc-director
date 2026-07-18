using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the session state-transition emitter (issue #1771, spine item 2). The claims that matter: the
/// four activity states map to ledger states and Starting/Exited do not; an event lands only on a real change
/// (a heartbeat repeat is skipped); a session that exits mid-wait gets one closing event so its wait is not
/// overcounted; a session that exits while active needs no closer; and a restarted session re-emits.
/// </summary>
public sealed class SessionStateEventEmitterTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private (SessionStateEventEmitter Emitter, GovernanceEventLedger Ledger) New()
    {
        var ledger = new GovernanceEventLedger(_h.Open());
        return (new SessionStateEventEmitter(ledger), ledger);
    }

    private List<string> StatesFor(GovernanceEventLedger ledger, string sessionId) =>
        ledger.List(sessionId: sessionId).Select(e => e.State).ToList();

    [Theory]
    [InlineData("Working", "active")]
    [InlineData("Idle", "idle")]
    [InlineData("WaitingForInput", "waiting-on-human")]
    [InlineData("WaitingForPerm", "waiting-on-permission")]
    [InlineData("waitingforinput", "waiting-on-human")] // case-insensitive
    public void MapState_maps_the_four_activity_states(string activity, string expected)
    {
        Assert.Equal(expected, SessionStateEventEmitter.MapState(activity));
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Exited")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void MapState_returns_null_for_non_ledger_states(string? activity)
    {
        Assert.Null(SessionStateEventEmitter.MapState(activity));
    }

    [Fact]
    public void Observe_emits_only_on_a_real_transition()
    {
        var (emitter, ledger) = New();
        emitter.Observe("s1", "Working");
        emitter.Observe("s1", "Working");          // heartbeat repeat - no new event
        emitter.Observe("s1", "WaitingForInput");
        emitter.Observe("s1", "WaitingForInput");  // repeat - no new event

        var states = StatesFor(ledger, "s1");
        Assert.Equal(2, states.Count);
        Assert.Contains(GovernanceEventState.Active, states);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, states);
    }

    [Fact]
    public void Observe_skips_starting_and_emits_nothing()
    {
        var (emitter, ledger) = New();
        emitter.Observe("s1", "Starting");
        Assert.Empty(ledger.List(sessionId: "s1"));
    }

    [Fact]
    public void Exit_while_waiting_emits_one_closing_event()
    {
        var (emitter, ledger) = New();
        emitter.Observe("s1", "WaitingForInput"); // open wait
        emitter.Observe("s1", "Exited");          // exits mid-wait -> close the interval honestly

        var states = StatesFor(ledger, "s1");
        Assert.Equal(2, states.Count);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, states);
        Assert.Contains(GovernanceEventState.Recovered, states); // the close-on-exit
    }

    [Fact]
    public void Exit_while_active_emits_no_closing_event()
    {
        var (emitter, ledger) = New();
        emitter.Observe("s1", "Working"); // active, no open wait
        emitter.Observe("s1", "Exited");  // no closer needed

        var states = StatesFor(ledger, "s1");
        Assert.Single(states);
        Assert.Equal(GovernanceEventState.Active, states[0]);
    }

    [Fact]
    public void A_restarted_session_after_exit_re_emits_its_first_state()
    {
        var (emitter, ledger) = New();
        emitter.Observe("s1", "Working");
        emitter.Observe("s1", "Exited");  // forgets s1 (no open wait, no closer)
        emitter.Observe("s1", "Working"); // a fresh transition after the guard was cleared

        var active = ledger.List(sessionId: "s1", state: GovernanceEventState.Active);
        Assert.Equal(2, active.Count);
    }
}
