using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Behavior tests for the final-build session features:
///   #3 terminal input  -> SendInput forwards raw bytes to the backend (the exact path the
///                          /stream WebSocket now drives).
///   #4 the prompt queue  -> a MANUAL holding list: it never auto-sends, whatever the activity
///                          state. The auto-drain this line used to describe was gated on a
///                          transition to Idle, which no producer has ever emitted, so it never
///                          ran in a real session and is now deleted (issue #1564).
///   #5 PTY resize       -> Resize no-ops on an unchanged size (the repaint-loop guard).
/// </summary>
public sealed class SessionInteractiveTests
{
    private static Session NewSession(RecordingBackend backend, ActivityState initial)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: initial,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning(); // so SendInput / drain aren't short-circuited by Exited/Failed
        return s;
    }

    // ---- #3 terminal input ----

    [Fact]
    public void SendInput_forwards_raw_bytes_to_the_backend()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        s.SendInput(new byte[] { 0x1b, (byte)'[', (byte)'A' }); // an Up-arrow escape sequence

        Assert.Single(backend.Writes);
        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'A' }, backend.Writes[0]);
    }

    // ================= the hold state machine =================
    // One field, three states: None / Held / DeferredHold. Design and diagram:
    // docs/new_architecture/session-state.html. These tests walk every cell of the
    // transition table in that document. Credit: the five deferred-hold cases come from pull request
    // #1512 (session "Gateway Cleanup - Tunnel-Only Migration"), re-pointed at the live ActivityState.

    // ---- THE RULE: a held session that starts working comes off hold, every time ----

    [Fact]
    public void HeldSession_ThatStartsWorking_LiftsTheHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.RequestHold(true);
        Assert.True(s.OnHold);

        // The agent comes back to life on its own - a background task landed, a sub-agent reported,
        // the model resumed. Nobody submitted anything.
        s.ApplyTerminalActivityState(ActivityState.Working);

        Assert.False(s.OnHold);                       // it cannot be parked and working at once
        Assert.Equal(HoldState.None, s.HoldState);
    }

    [Fact]
    public void HeldSession_ThatStartsWorking_LiftsEvenWithNoSubmissionBehindIt()
    {
        // THE REGRESSION THIS MACHINE EXISTS TO KILL. Before, the lift was gated on a turn latch that
        // was armed only by Enter and destroyed by any 10 seconds of terminal quiet - so an ordinary
        // slow command mid-turn left the session able to work forever while still reading "Snoozed".
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // a 10s quiet gap MID-turn
        s.ApplyTerminalActivityState(ActivityState.Working);         // output resumes
        s.RequestHold(true);                                         // user snoozes it -> deferred
        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // the turn really ends -> parks
        Assert.True(s.OnHold);

        s.ApplyTerminalActivityState(ActivityState.Working);         // it wakes up again
        Assert.False(s.OnHold);                                      // and comes back. Every time.
    }

    [Fact]
    public void HeldSession_LeftAlone_StaysHeld()
    {
        // The other half of the rule: silence is not work. A held session with no output sits held -
        // measured on the live fleet, an idle Claude Code session is byte-silent for tens of minutes,
        // so it never reaches Working and never lifts.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.WaitingForInput);
        s.RequestHold(true);

        s.ApplyTerminalActivityState(ActivityState.Idle); // settling around, no work
        Assert.True(s.OnHold);
        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.True(s.OnHold);
    }

    // ---- #470: a fresh submission supersedes any hold ----

    [Fact]
    public async Task SendTextAsync_OnHeldSession_ClearsOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.RequestHold(true);

        await s.SendTextAsync("hello");

        Assert.False(s.OnHold);
    }

    [Fact]
    public void SendInput_WithSubmitByte_ClearsOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.RequestHold(true);

        s.SendInput(new byte[] { (byte)'h', (byte)'i', 0x0D }); // text + CR (Enter)

        Assert.False(s.OnHold);
    }

    [Fact]
    public void SendInput_BareKeystroke_LeavesOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.RequestHold(true);

        s.SendInput(new byte[] { (byte)'h', (byte)'i' }); // composing - no CR/LF

        Assert.True(s.OnHold); // still held - a bare keystroke is not a submitted turn
    }

    [Fact]
    public async Task SendTextAsync_OnHeldSession_RaisesOnHoldChangedOnceWithFalse()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.RequestHold(true);
        var events = new List<bool>();
        s.OnHoldChanged += value => events.Add(value);

        await s.SendTextAsync("hello");

        Assert.Single(events);          // exactly once
        Assert.False(events[0]);        // with value false
    }

    // ---- DeferredHold: "hold my session when it finishes what it is doing" (credit: #1512) ----

    [Fact]
    public async Task RequestHold_MidTurn_IsDeferred_ThenAppliesDurablyAtRedSettle()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("do the thing");
        var outcome = s.RequestHold(true);      // user says "hold this" WHILE it is working

        Assert.Equal(Session.HoldOutcome.Pending, outcome);
        Assert.Equal(HoldState.DeferredHold, s.HoldState);
        Assert.False(s.OnHold);                 // not parked yet - it is still visibly working

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // turn ends -> red "needs you"
        Assert.True(s.OnHold);                  // the deferral landed
        Assert.Equal(HoldState.Held, s.HoldState);
    }

    [Fact]
    public async Task RequestHold_MidTurn_AppliesAtIdleSettleToo()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("go");
        s.RequestHold(true);

        s.ApplyTerminalActivityState(ActivityState.Idle); // turn ends at ready, not "needs you"
        Assert.True(s.OnHold);                            // still parks held
    }

    [Fact]
    public void RequestHold_WhenSettled_HoldsImmediately()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        var outcome = s.RequestHold(true); // not working
        Assert.Equal(Session.HoldOutcome.Held, outcome);
        Assert.True(s.OnHold);
    }

    [Fact]
    public async Task RequestHold_False_ReleasesAndClearsAPendingDefer()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("go");
        s.RequestHold(true);                          // deferred
        var outcome = s.RequestHold(false);           // user changes their mind before it lands

        Assert.Equal(Session.HoldOutcome.Released, outcome);
        Assert.Equal(HoldState.None, s.HoldState);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // turn ends
        Assert.False(s.OnHold); // the cleared deferral is NOT resurrected
    }

    [Fact]
    public async Task RequestHold_PendingDefer_IsSupersededByANewSubmission()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("first");
        s.RequestHold(true);             // deferred
        await s.SendTextAsync("second"); // a fresh submission - the user is driving again

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // that turn ends
        Assert.False(s.OnHold); // the superseded deferral did not apply
    }

    [Fact]
    public async Task RequestHold_Deferred_IsDroppedIfTheSessionExits()
    {
        // A deferral has nothing to come back to once the agent is gone; parking a dead session would
        // just hide it behind a "Snoozed" label forever.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("go");
        s.RequestHold(true);

        s.ApplyTerminalActivityState(ActivityState.Exited);
        Assert.False(s.OnHold);
        Assert.Equal(HoldState.None, s.HoldState);
    }

    // ---- Defect 21: a snoozed session that exits reads Exited, never "Snoozed" ----

    [Fact]
    public void RequestHold_Landed_IsAlsoClearedIfTheSessionExits()
    {
        // DEFECT 21. The rule above (drop a DEFERRED hold on exit) carried the reasoning in its own
        // comment - "parking a dead session would just hide it behind a 'Snoozed' label forever" - and was
        // then applied only to the deferred case. A session that was ALREADY Held kept OnHold=true on
        // exit, the fold checks OnHold before the base activity colour, and so the row read "Snoozed"
        // forever: the exact outcome the neighbouring comment forbids.
        //
        // THE RULING (owner, 14 July 2026): a snoozed session that exits reads Exited. A dead session
        // never hides behind a Snoozed label.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.WaitingForInput);
        s.RequestHold(true);
        Assert.Equal(HoldState.Held, s.HoldState);   // landed immediately - it was not working

        s.ApplyTerminalActivityState(ActivityState.Exited);

        Assert.Equal(HoldState.None, s.HoldState);
        Assert.False(s.OnHold);   // -> the fold falls through to the base colour: grey "Exited"
    }

    // ---- Defect 22: the hold survives a Director restart ----

    [Fact]
    public void RestoreHoldState_Held_OnASettledSession_ComesBackHeld()
    {
        // DEFECT 22: before this, HoldState was runtime-only - a restart forgot every snooze, so a
        // 12-hour snooze silently became no snooze at all while the Gateway's timer entry lived on,
        // pointing at a session that was not held.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.WaitingForInput);

        s.RestoreHoldState(HoldState.Held);

        Assert.Equal(HoldState.Held, s.HoldState);
        Assert.True(s.OnHold);
    }

    [Fact]
    public void RestoreHoldState_DeferredHold_OnASettledSession_Lands()
    {
        // THE RULING (owner, 14 July 2026): persist the hold state, and LAND the deferral on restart if
        // the session is not working. It follows from the ruling that the clock starts when the work ends
        // - the deferral was waiting for a turn to finish, and the Director that died finished it.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.WaitingForInput);

        s.RestoreHoldState(HoldState.DeferredHold);

        Assert.Equal(HoldState.Held, s.HoldState);
        Assert.True(s.OnHold);
    }

    [Fact]
    public void RestoreHoldState_DeferredHold_OnAWorkingSession_StaysDeferred()
    {
        // Still working, so the deferral is still waiting for exactly what it was always waiting for.
        // The settle edge lands it as usual, and meanwhile the session is blue and reads "Working".
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        s.RestoreHoldState(HoldState.DeferredHold);

        Assert.Equal(HoldState.DeferredHold, s.HoldState);
        Assert.False(s.OnHold);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.Equal(HoldState.Held, s.HoldState);   // lands at the settle, as it would have done
    }

    [Fact]
    public void RestoreHoldState_Held_OnAWorkingSession_IsLifted()
    {
        // Working ALWAYS clears a hold - the load-bearing rule of the whole machine. A restore is not an
        // exception to it: a session that came back working is not parked, whatever the store said.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        s.RestoreHoldState(HoldState.Held);

        Assert.Equal(HoldState.None, s.HoldState);
        Assert.False(s.OnHold);
    }

    [Fact]
    public void RestoreHoldState_OnAnExitedSession_DropsTheHold()
    {
        // The exit rule, applied at restore: there is no turn to come back to, and a dead session must
        // never hide behind a "Snoozed" label (defect 21's ruling).
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Exited);

        s.RestoreHoldState(HoldState.Held);
        Assert.Equal(HoldState.None, s.HoldState);

        s.RestoreHoldState(HoldState.DeferredHold);
        Assert.Equal(HoldState.None, s.HoldState);
    }

    [Fact]
    public void RequestHold_WhileWorking_DoesNotRaiseOnHoldChanged_ButDoesRaiseHoldStateChanged()
    {
        // None -> DeferredHold parks nothing, so the "is it parked?" listeners (rail strip, FIFO
        // conductor) must not fire. The push to the Gateway must, because the LABEL changes.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        var parked = new List<bool>();
        var states = new List<HoldState>();
        s.OnHoldChanged += v => parked.Add(v);
        s.HoldStateChanged += st => states.Add(st);

        s.RequestHold(true);

        Assert.Empty(parked);
        Assert.Equal(new[] { HoldState.DeferredHold }, states);
    }

    // ---- #5 PTY resize guard ----

    [Fact]
    public void Resize_changed_size_calls_backend_unchanged_is_noop()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        s.Resize(80, 24);
        s.Resize(80, 24); // identical -> guarded, must NOT reach the backend again
        s.Resize(120, 30);

        Assert.Equal(2, backend.Resizes.Count);
        Assert.Equal((80, 24), backend.Resizes[0]);
        Assert.Equal((120, 30), backend.Resizes[1]);
    }

    // ---- the prompt queue is a MANUAL holding list, and always has been ----

    // These replace three tests that certified an auto-drain which has never run in a real session. They
    // passed for fourteen months by calling ApplyTerminalActivityState(ActivityState.Idle) directly - a state
    // NOTHING in the product has ever assigned, so the transition they relied on could not occur outside the
    // test. The old gate is deleted (see Session.SetActivityState), and what is pinned here is the behaviour
    // users actually have and the product actually describes: a queue you send from explicitly.

    /// <summary>
    /// The queue never auto-sends, on ANY activity state. Deliberately a theory over EVERY member of the
    /// enum rather than the one state the detector happens to emit today: that makes the guarantee immune to
    /// the producer changing which state it reports at a turn end - the exact coupling that let the original
    /// drain sit dead behind a state no producer emitted, with a green test in front of it.
    /// </summary>
    [Theory]
    [InlineData(ActivityState.Starting)]
    [InlineData(ActivityState.Idle)]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Exited)]
    public async Task Queue_neverAutoSends_onAnyActivityState(ActivityState state)
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("queued work");

        s.ApplyTerminalActivityState(state);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);          // nothing was fired at the session
        Assert.Equal(1, s.PromptQueue.Count);     // and the item is still there, waiting to be sent
    }

    /// <summary>
    /// The turn end specifically. This is the moment a queued prompt would have gone out, and the moment it
    /// must not: the detector reports WaitingForInput for BOTH "finished cleanly" and "blocked on a question"
    /// and explicitly refuses to tell them apart, so an auto-send here could answer a question the agent
    /// asked with unrelated text - a silent wrong action. Auto-send needs a real turn-end classifier first
    /// (issue #1564).
    /// </summary>
    [Fact]
    public async Task Queue_doesNotAutoSend_atATurnEnd_whichCouldAnswerTheAgentsOwnQuestion()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("also update the README");

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    /// <summary>The queue still does what it is for: the user sends an item, explicitly, whenever they want.</summary>
    [Fact]
    public async Task Queue_itemIsSentWhenTheUserSendsIt()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        var item = s.PromptQueue.Enqueue("first");
        s.PromptQueue.Enqueue("second");

        await s.SendTextAsync(item.Text);
        s.PromptQueue.Remove(item.Id);

        Assert.Equal("first", backend.SentTexts[0]);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>An ISessionBackend that records what the Session sends it.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public List<string> SentTexts { get; } = new();
        public List<(short cols, short rows)> Resizes { get; } = new();

        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) { SentTexts.Add(text); return Task.CompletedTask; }
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) => Resizes.Add((cols, rows));
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
