using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the session rail's reading of state (mission "Session State Truth", phase 4).
///
/// NOTHING defended any of these before - the rail's "N need you" count, its grey brushes, and its
/// palette were all untested, which is how a snoozed session came to sit grey and labelled "Snoozed"
/// underneath a header reading "1 need you". Three readings of one session, and nothing reconciled
/// them.
///
/// These build a REAL <see cref="Session"/> and a REAL <see cref="SessionViewModel"/> and read the
/// same properties the XAML binds, so they exercise the production fold
/// (SessionViewModel.FoldInput -> ControlEndpoints.Map -> SessionOrdering) rather than a
/// re-implementation of it. Every one of them has been watched to FAIL with the reported symptom
/// before the fix was restored.
///
/// Design: docs/new_architecture/session-state.html
/// </summary>
public sealed class SessionRailStateTests
{
    /// <summary>
    /// A session parked at a turn end with the wingman's raw colour already written red - i.e. the
    /// ordinary "Claude is waiting on you" session, and the exact shape the old count mis-read once
    /// it was snoozed.
    /// </summary>
    private static Session RedAtTurnEnd()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);

        // ActivityState defaults to WaitingForInput - a real turn end, no private setter needed.
        // IsBrandNew must be cleared or the fold answers "green" (ready) and the test would pass for
        // entirely the wrong reason: a brand-new session is not red at all.
        session.IsBrandNew = false;
        session.SetStatusColor("red", "needs you");
        return session;
    }

    // ===== Defect 2, leg 1: the COUNT reads the fold, not the raw cooked colour =====

    [Fact]
    public void NeedsYou_SessionAtTurnEnd_IsCounted()
    {
        // The control. If this ever goes false the fix has broken the feature rather than the defect.
        var vm = new SessionViewModel(RedAtTurnEnd());

        Assert.True(vm.NeedsYou);
    }

    [Fact]
    public void NeedsYou_SnoozedSessionAtTurnEnd_IsNotCounted()
    {
        var session = RedAtTurnEnd();
        // The real Snooze path the user's button calls, not a poke at internals. A settled session
        // holds immediately (a working one would defer) - assert that, so this can never silently
        // become a test of a session that was never actually held.
        Assert.Equal(Session.HoldOutcome.Held, session.RequestHold(true));
        var vm = new SessionViewModel(session);

        // The raw fact the old predicate read is STILL "red" - a snoozed session genuinely IS at a
        // turn end, which is exactly why counting the raw colour counted it. This assertion is the
        // whole point: the defect is not that the colour is wrong, it is that the count read it.
        Assert.Equal("red", session.StatusColor);

        // ...and the fold says otherwise, so the header must not nag.
        Assert.False(vm.NeedsYou);

        // The row it is counted against says "Snoozed" and renders grey. All three readings agree.
        Assert.Equal("Snoozed", vm.ActivityLabel);
        Assert.Equal(Color.Parse(StatusPalette.Grey), ((ISolidColorBrush)vm.StatusColorBrush).Color);
    }

    // ===== Defect 2, leg 2: the count is NOTIFIED when the verdict moves, not only when a colour is written =====

    [Fact]
    public void NeedsYou_RaisesChangeNotification_WhenHoldChanges()
    {
        // The header subscribes to this property. It used to hang off Session.OnStatusColorChanged
        // alone - but snoozing writes no colour, so the count went stale until the 15-second git
        // timer happened to fire, leaving "1 need you" above a grey "Snoozed" row for up to fifteen
        // seconds.
        var session = RedAtTurnEnd();
        var vm = new SessionViewModel(session);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(Session.HoldOutcome.Held, session.RequestHold(true));
        Dispatcher.UIThread.RunJobs();   // the view-model marshals its repaints; pump them

        Assert.Contains(nameof(SessionViewModel.NeedsYou), raised);
        Assert.False(vm.NeedsYou);       // and the new value is the folded one
    }

    // ===== Defect 17: ONE grey. The rail must not re-split it by re-reading a raw flag =====

    [Fact]
    public void StatusColorBrush_SnoozedSession_IsTheOneGrey_NotTheOldLightGrey()
    {
        var session = RedAtTurnEnd();
        Assert.Equal(Session.HoldOutcome.Held, session.RequestHold(true));
        var vm = new SessionViewModel(session);

        var actual = ((ISolidColorBrush)vm.StatusColorBrush).Color;

        Assert.Equal(Color.Parse(StatusPalette.Grey), actual);
        // The strays that died. #9CA3AF was the on-hold light grey the rail invented by re-reading
        // Session.OnHold; #6A6A6A was its exited/unknown grey. Neither is in any palette now, and the
        // phone never had either - which is why the devices disagreed.
        Assert.NotEqual(Color.Parse("#9CA3AF"), actual);
        Assert.NotEqual(Color.Parse("#6A6A6A"), actual);
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
