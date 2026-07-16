using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
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
        session.ApplyGatewayHold(HoldState.Held);
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

        session.ApplyGatewayHold(HoldState.Held);
        Dispatcher.UIThread.RunJobs();   // the view-model marshals its repaints; pump them

        Assert.Contains(nameof(SessionViewModel.NeedsYou), raised);
        Assert.False(vm.NeedsYou);       // and the new value is the folded one
    }

    // ===== Defect 17: ONE grey. The rail must not re-split it by re-reading a raw flag =====

    [Fact]
    public void StatusColorBrush_SnoozedSession_IsTheOneGrey_NotTheOldLightGrey()
    {
        var session = RedAtTurnEnd();
        session.ApplyGatewayHold(HoldState.Held);
        var vm = new SessionViewModel(session);

        var actual = ((ISolidColorBrush)vm.StatusColorBrush).Color;

        Assert.Equal(Color.Parse(StatusPalette.Grey), actual);
        // The strays that died. #9CA3AF was the on-hold light grey the rail invented by re-reading
        // Session.OnHold; #6A6A6A was its exited/unknown grey. Neither is in any palette now, and the
        // phone never had either - which is why the devices disagreed.
        Assert.NotEqual(Color.Parse("#9CA3AF"), actual);
        Assert.NotEqual(Color.Parse("#6A6A6A"), actual);
    }

    // ===== Gap 1: the role BADGE reads the Gateway's stamp, never a local guess =====

    /// <summary>
    /// Before any Gateway has spoken, the badge has NO answer - not the answer "Standalone".
    ///
    /// This asserts the VALUE, not just the absence of a glyph, and that is deliberate. The old field
    /// defaulted to Standalone, which also renders no glyph, so an "is the badge hidden?" assertion
    /// passes just as happily against the defect and would prove nothing. The distinction that matters
    /// is between "the Gateway has not classified this session yet" and "the Gateway classified this
    /// session as Standalone": identical on screen, opposite in meaning, and the old default asserted
    /// the second while knowing only the first.
    /// </summary>
    [Fact]
    public void ResolvedRole_BeforeAnyGatewayStamp_IsUnknown_NotAssertedStandalone()
    {
        var vm = new SessionViewModel(RedAtTurnEnd());

        Assert.Null(vm.ResolvedRole);
        Assert.False(vm.HasRoleGlyph);
        Assert.Equal("", vm.RoleGlyphText);
        Assert.Equal("", vm.RoleTooltip);
    }

    /// <summary>
    /// THE GAP 1 DEFECT ITSELF: the badge must follow the GATEWAY's stamp, on a session this Director
    /// cannot classify for itself.
    ///
    /// The session below is controlled by a controller that is NOT in this Director's roster - the
    /// ordinary cross-machine case, and the one the down-channel exists to serve. The deleted
    /// SessionManager.ResolveLocalRole scored exactly this session "Standalone" with total confidence,
    /// because it could only see the local roster and the controller was not in it. So the colour folded
    /// the Gateway's "Worker" and suppressed the red, while the glyph beside it showed the Director's
    /// local guess. One row, two authorities, and they disagreed.
    ///
    /// Watched failing on revert: with the stamped property restored, RoleGlyphText is "" here, because
    /// nothing in the view model reads GatewayResolvedRole at all - the badge only ever moved when
    /// MainWindow stamped it from the local fleet.
    /// </summary>
    [Fact]
    public void ResolvedRole_FollowsTheGatewayStamp_EvenWhenTheLocalRosterCannotKnowIt()
    {
        var session = RedAtTurnEnd();
        // No controller by this id exists on this Director - it is a session on another machine. The
        // local resolver's answer for this shape was Standalone; the Gateway's is Worker.
        session.ControllerSessionId = Guid.NewGuid();
        var vm = new SessionViewModel(session);

        session.SetGatewayResolvedRole(SessionRoles.Worker);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SessionRoles.Worker, vm.ResolvedRole);
        Assert.True(vm.HasRoleGlyph);
        Assert.Equal("W", vm.RoleGlyphText);
        Assert.Equal("Worker", vm.RoleTooltip);
    }

    /// <summary>
    /// The badge and the dot must never contradict each other, because they now read the SAME fact.
    ///
    /// This is the invariant gap 1 was really about: not "the badge is wrong" but "the row disagrees with
    /// itself". The Gateway names this session a Worker; the fold suppresses its red to supporting AND
    /// the glyph says "W", in the same repaint. Before, the first happened without the second.
    /// </summary>
    [Fact]
    public void AStampedWorker_MovesTheBadgeAndTheDotTogether_SoTheRowAgreesWithItself()
    {
        var session = RedAtTurnEnd();
        session.ControllerSessionId = Guid.NewGuid();
        var vm = new SessionViewModel(session);

        // Before the stamp: an ordinary red session that wants you, and no badge to explain it.
        Assert.True(vm.NeedsYou);
        Assert.False(vm.HasRoleGlyph);

        session.SetGatewayResolvedRole(SessionRoles.Worker);
        Dispatcher.UIThread.RunJobs();

        // After: the dot stops nagging and the glyph says why, together.
        Assert.False(vm.NeedsYou);
        Assert.Equal("W", vm.RoleGlyphText);
    }

    /// <summary>
    /// Clearing the stamp returns the badge to "no answer" rather than stranding the last role on screen.
    /// The Gateway clears a role by sending null (SetGatewayResolvedRole normalizes blank to null), and a
    /// badge that kept saying "W" after the Gateway withdrew the claim is a stale lie of exactly the kind
    /// this mission exists to end.
    /// </summary>
    [Fact]
    public void ResolvedRole_WhenTheGatewayClearsTheStamp_ReturnsToUnknown()
    {
        var session = RedAtTurnEnd();
        var vm = new SessionViewModel(session);
        session.SetGatewayResolvedRole(SessionRoles.Worker);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("W", vm.RoleGlyphText);   // the precondition, so this cannot pass vacuously

        session.SetGatewayResolvedRole(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ResolvedRole);
        Assert.False(vm.HasRoleGlyph);
        Assert.Equal("", vm.RoleGlyphText);
    }

    // ===== Defect 5: the role stamp must move the WHOLE row, not just the dot =====

    /// <summary>
    /// The row a controlled worker actually renders once the Gateway names its role. This is the
    /// PROJECTION, not the model event: GatewayResolvedRoleSignalTests prove Session raises the change,
    /// and raising it means nothing if the view model does not re-read what the fold feeds.
    ///
    /// The first fix here raised only the brush, the reason and the count - so the dot went "supporting"
    /// while the row text still read "Needs you" beside a running waiting timer. One row disagreeing with
    /// itself is worse than the stale row it replaced: it looks deliberate. Caught by review, not by the
    /// suite, because nothing asserted the row AS A WHOLE.
    /// </summary>
    [Fact]
    public void AStampedWorker_MovesEveryFoldFedFieldTogether_NotJustTheDot()
    {
        var session = RedAtTurnEnd();
        // The waiting timer only shows once an explain has been cached - that is what stamps the "waiting
        // since" instant. Without it HasWaitingDuration is false for a reason unrelated to the role, and
        // this test would prove nothing about the fix.
        session.SetCachedExplain("waiting on you", model: "test");
        var vm = new SessionViewModel(session);

        // Before: an ordinary red session that wants you, timer running.
        Assert.True(vm.NeedsYou);
        Assert.True(vm.HasWaitingDuration);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };

        session.SetGatewayResolvedRole("Worker");
        Dispatcher.UIThread.RunJobs();

        // Every field the fold feeds must be re-read. Name them individually: a missing one is exactly
        // the defect, and asserting "some property changed" would pass while the label stayed wrong.
        Assert.Contains(nameof(SessionViewModel.StatusColorBrush), changed);
        Assert.Contains(nameof(SessionViewModel.ActivityLabel), changed);
        Assert.Contains(nameof(SessionViewModel.HasWaitingDuration), changed);
        Assert.Contains(nameof(SessionViewModel.WaitingDurationLabel), changed);
        Assert.Contains(nameof(SessionViewModel.NeedsYou), changed);
        Assert.Contains(nameof(SessionViewModel.StatusReason), changed);

        // And the row must actually AGREE with itself afterwards - the point of raising them at all.
        Assert.False(vm.NeedsYou);
        Assert.False(vm.HasWaitingDuration);
        Assert.DoesNotContain("Needs you", vm.ActivityLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EVERY fold input must move the whole row, not just the one its author happened to think of.
    ///
    /// Each handler in SessionViewModel used to keep its own list of properties to raise, and they all
    /// disagreed - hold raised seven, activity three, the cached explain two, the role stamp three. Each
    /// list was a private chance to miss one, and missing one renders a HALF-updated row: the dot moves,
    /// the text beside it does not. Three fold inputs (background task, dictation, auto-explain) had no
    /// subscription at all, so they moved no part of the row.
    ///
    /// This drives each fold input through a REAL Session and REAL SessionViewModel and demands the same
    /// invariant of all of them, so a future handler that re-grows its own list fails here rather than in
    /// someone's rail. Found by review of pull request 1598 after two of these were fixed one at a time.
    /// </summary>
    [Theory]
    [InlineData("role")]
    [InlineData("activity")]
    [InlineData("hold")]
    [InlineData("dictation")]
    [InlineData("background")]
    [InlineData("explaining")]
    [InlineData("wingman-gate")]
    public void EveryFoldInput_RaisesTheWholeProjection(string foldInput)
    {
        var session = RedAtTurnEnd();
        session.SetCachedExplain("waiting on you", model: "test");
        // The gate case needs something for the gate to gate: parked on its own background task, which the
        // fold answers purple ONLY while the Wingman is on. Turning it off must move the whole row to red.
        if (foldInput == "wingman-gate")
        {
            session.WingmanEnabled = true;
            session.SetBackgroundRunning(true, "running in background");
        }
        var vm = new SessionViewModel(session);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };

        switch (foldInput)
        {
            case "role": session.SetGatewayResolvedRole("Worker"); break;
            case "activity": session.ApplyTerminalActivityState(ActivityState.Working); break;
            case "hold": session.ApplyGatewayHold(HoldState.Held); break;
            case "dictation": session.IsTranscribing = true; break;
            case "background": session.SetBackgroundRunning(true, "running in background"); break;
            case "explaining": session.IsExplaining = true; break;
            case "wingman-gate": session.WingmanEnabled = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(foldInput), foldInput, "unknown fold input");
        }
        Dispatcher.UIThread.RunJobs();

        // Named individually and deliberately: asserting "something changed" would pass with the row text
        // stale, which IS the defect. These are exactly the properties whose getters fold.
        Assert.Contains(nameof(SessionViewModel.StatusColorBrush), changed);
        Assert.Contains(nameof(SessionViewModel.StatusReason), changed);
        Assert.Contains(nameof(SessionViewModel.ActivityLabel), changed);
        Assert.Contains(nameof(SessionViewModel.NeedsYou), changed);
        Assert.Contains(nameof(SessionViewModel.HasWaitingDuration), changed);
        Assert.Contains(nameof(SessionViewModel.WaitingDurationLabel), changed);

        // The role badge (gap 1). It reads Session.GatewayResolvedRole - the same Gateway-owned fact the
        // colour folds - so it belongs to the row's projection and must move with it. Demanded of EVERY
        // fold input, not just "role", for the reason this theory exists at all: the moment the badge is
        // raised from one handler's private list, it is one edit away from being the field that gets
        // missed, and a glyph contradicting the dot beside it is gap 1 returning.
        Assert.Contains(nameof(SessionViewModel.ResolvedRole), changed);
        Assert.Contains(nameof(SessionViewModel.HasRoleGlyph), changed);
        Assert.Contains(nameof(SessionViewModel.RoleGlyphText), changed);
        Assert.Contains(nameof(SessionViewModel.RoleTooltip), changed);
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
