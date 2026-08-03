using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #959, re-broken and now restored. A crashed session must show the deep red #B91C1C - deliberately
/// darker than the bright "needs you" red - so a session that DIED is never mistaken for one that finished.
/// It had been rendering as an ordinary grey "Exited", byte-identical to a clean exit, on every surface.
///
/// The mechanism, because it is not the obvious one: a crash was NEVER modelled in ActivityState - a crashed
/// session is "Exited" like any other. It lives on Session.Crashed, and the PRODUCER never stopped working.
/// What broke was the transport and the fold. SessionDto carried no crash fact, and SessionOrdering computes
/// colour from RAW facts only ("the Gateway is the single fold and reads the Director's cooked StatusColor
/// for NOTHING"), so there was simply no crash fact on the wire for it to read. Two independent regressions
/// of the same behaviour: #1177 took it from the Cockpit and the phone, #1537 took it from the desktop rail
/// when the rail joined the shared fold. ErrorStatusBrush, the "error" switch arm and the #B91C1C palette
/// entry all survived as orphans, which is why the support still LOOKED present.
///
/// These tests drive the REAL producer - a backend raising a real process exit into
/// Session.OnBackendProcessExited - then the REAL wire mapper, then the REAL fold. Nothing sets Crashed by
/// hand, so the tests cannot pass on a fact production never emits.
/// </summary>
public sealed class CrashedSessionColorTests
{
    /// <summary>A backend whose process exit can be driven, so the real crash decision runs.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // required by the interface, unused here
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        public void RaiseExit(int code)
        {
            _exited = true;
            ProcessExited?.Invoke(code);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Runs a session to a real process exit and returns what the wire carries - the exact SessionDto the
    /// Gateway aggregates AND the desktop rail folds (both go through ControlEndpoints.Map).
    /// </summary>
    private static SessionDto ExitedSessionOnTheWire(int exitCode, ActivityState stateBeforeExit)
    {
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        session.ApplyTerminalActivityState(stateBeforeExit);

        backend.RaiseExit(exitCode);   // the real producer decides crash-vs-clean

        return ControlEndpoints.Map(session, directorId: "");
    }

    [Fact]
    public void Crash_withNonZeroExit_foldsToTheDeepRed_notGrey()
    {
        var dto = ExitedSessionOnTheWire(exitCode: 1, stateBeforeExit: ActivityState.WaitingForInput);

        Assert.True(dto.Crashed);                                  // the fact reached the wire
        Assert.Equal("Exited", dto.ActivityState);                 // ...and ActivityState still cannot say it
        Assert.Equal("error", SessionOrdering.EffectiveColor(dto));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(dto));
    }

    [Fact]
    public void Crash_withExitZero_whileWorking_foldsToTheDeepRed()
    {
        // Some crashes exit 0, so the exit code alone is not enough - dropping out mid-work is the tell.
        var dto = ExitedSessionOnTheWire(exitCode: 0, stateBeforeExit: ActivityState.Working);

        Assert.True(dto.Crashed);
        Assert.Equal("error", SessionOrdering.EffectiveColor(dto));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(dto));
    }

    [Fact]
    public void CleanExit_isUnchanged_stillGreyAndStillReadsExited()
    {
        // The other half of the contract: a session that finished on purpose must NOT gain a crash colour.
        var dto = ExitedSessionOnTheWire(exitCode: 0, stateBeforeExit: ActivityState.WaitingForInput);

        Assert.False(dto.Crashed);
        Assert.Equal("grey", SessionOrdering.EffectiveColor(dto));
        Assert.Equal("Exited", SessionOrdering.StateLabel(dto));
    }

    [Fact]
    public void Crash_andCleanExit_areDistinguishable_theWholePointOf959()
    {
        var crashed = ExitedSessionOnTheWire(exitCode: 1, stateBeforeExit: ActivityState.WaitingForInput);
        var clean = ExitedSessionOnTheWire(exitCode: 0, stateBeforeExit: ActivityState.WaitingForInput);

        Assert.NotEqual(SessionOrdering.EffectiveColor(clean), SessionOrdering.EffectiveColor(crashed));
        Assert.NotEqual(SessionOrdering.StateLabel(clean), SessionOrdering.StateLabel(crashed));
    }

    [Fact]
    public void Crash_doesNotBecomeNeedsYou_theDeepRedStaysDistinctFromTheBrightRed()
    {
        // #959 chose a SEPARATE deep red on purpose. If a crash folded to plain "red" it would be
        // indistinguishable from a session waiting on the human - a different lie, not a fix.
        var dto = ExitedSessionOnTheWire(exitCode: 1, stateBeforeExit: ActivityState.Working);

        Assert.NotEqual("red", SessionOrdering.EffectiveColor(dto));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(dto));   // triage is unchanged
    }
}
