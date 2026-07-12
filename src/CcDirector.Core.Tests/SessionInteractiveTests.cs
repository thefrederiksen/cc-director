using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Behavior tests for the final-build session features:
///   #3 terminal input  -> SendInput forwards raw bytes to the backend (the exact path the
///                          /stream WebSocket now drives).
///   #4 queue auto-drain -> the next queued prompt is sent when the session goes Idle, gated
///                          by OnHold and never on WaitingForInput.
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

    // ---- #470 typing into a held session takes it off Hold ----

    [Fact]
    public async Task SendTextAsync_OnHeldSession_ClearsOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;

        await s.SendTextAsync("hello");

        Assert.False(s.OnHold);
    }

    [Fact]
    public void SendInput_WithSubmitByte_ClearsOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;

        s.SendInput(new byte[] { (byte)'h', (byte)'i', 0x0D }); // text + CR (Enter)

        Assert.False(s.OnHold);
    }

    [Fact]
    public void SendInput_BareKeystroke_LeavesOnHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;

        s.SendInput(new byte[] { (byte)'h', (byte)'i' }); // composing - no CR/LF

        Assert.True(s.OnHold); // still held - a bare keystroke is not a submitted turn
    }

    [Fact]
    public async Task SendTextAsync_OnHeldSession_RaisesOnHoldChangedOnceWithFalse()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;
        var events = new List<bool>();
        s.OnHoldChanged += value => events.Add(value);

        await s.SendTextAsync("hello");

        Assert.Single(events);          // exactly once
        Assert.False(events[0]);        // with value false
    }

    // ---- Hold auto-lift on turns: the OUT-of-a-turn edge ----
    // A snoozed session must come off hold when the agent FINISHES a real turn and lands at a
    // "needs you" settle - so a session parked mid-work cannot silently sit there needing the user.
    // The lift is anchored to a real submission, so the periodic cosmetic terminal repaints (which
    // reach WaitingForInput with no submission behind them) never break the hold.

    [Fact]
    public async Task TurnEnd_AfterMidWorkSnooze_LiftsHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("do the thing"); // real submission arms the turn (also clears hold on the way in)
        s.OnHold = true;                        // user snoozes WHILE it is still working

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // agent finishes -> needs you

        Assert.False(s.OnHold); // the completed turn took it off hold
    }

    [Fact]
    public async Task TurnEnd_WaitingForPerm_LiftsHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("run it");
        s.OnHold = true;

        s.ApplyTerminalActivityState(ActivityState.WaitingForPerm); // agent stops to ask permission

        Assert.False(s.OnHold);
    }

    [Fact]
    public void CosmeticRepaint_OnHeldIdleSession_KeepsHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.OnHold = true; // parked while idle - no turn in flight

        // A periodic Claude repaint: a byte blip to Working, then settling straight back to needs-you.
        s.ApplyTerminalActivityState(ActivityState.Working);
        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.True(s.OnHold); // no real submission behind it -> the hold survives the repaint
    }

    [Fact]
    public async Task TurnEndingAtIdle_ConsumesArming_SoLaterRepaintCannotLiftHold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        await s.SendTextAsync("go"); // arms the turn
        s.OnHold = true;

        s.ApplyTerminalActivityState(ActivityState.Idle); // quiet return to ready - not a "needs you" settle
        Assert.True(s.OnHold);       // a turn ending at ready does not surface a held session

        // The arming was consumed at the Idle settle, so a later cosmetic repaint cannot pose as a turn-end.
        s.ApplyTerminalActivityState(ActivityState.Working);
        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.True(s.OnHold);
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

    // ---- #4 queue auto-drain ----

    [Fact]
    public async Task Queue_auto_drains_one_item_when_session_goes_idle()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("first");
        s.PromptQueue.Enqueue("second");

        s.ApplyTerminalActivityState(ActivityState.Idle); // transition triggers the drain

        await WaitUntil(() => backend.SentTexts.Count >= 1);
        Assert.Equal("first", backend.SentTexts[0]); // FIFO
        Assert.Equal(1, s.PromptQueue.Count);          // exactly one drained per Idle
    }

    [Fact]
    public async Task Queue_does_not_drain_when_on_hold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;
        s.PromptQueue.Enqueue("held");

        s.ApplyTerminalActivityState(ActivityState.Idle);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    [Fact]
    public async Task Queue_does_not_drain_on_waiting_for_input()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("answer?");

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // Claude is asking - do NOT auto-answer

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
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
