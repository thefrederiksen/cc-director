using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Gap 2: the FIFO takeover's queue must be built by the SHARED FOLD, not by the window re-deciding
/// state for itself.
///
/// NOTHING defended this window before. It filtered on the Director's raw cooked colour
/// (<c>StatusColor == "red" &amp;&amp; !OnHold &amp;&amp; not exited/failed</c>) while the rail beside it,
/// the phone and the Cockpit all folded <c>ControlEndpoints.Map</c> through <see cref="SessionOrdering"/> -
/// so the queue could hand the owner a session the rail was not calling red. That is the mission's whole
/// defect class (two readings of one session, nothing reconciling them) living one window over.
///
/// These drive REAL <see cref="Session"/> objects through the REAL production queue builder
/// (FifoWindow.BuildQueue), so they exercise the shipped fold rather than a re-implementation of it.
/// Every one has been watched FAILING with the reported symptom against the old raw-colour predicate.
/// </summary>
public sealed class FifoQueueFoldTests
{
    /// <summary>A session parked at a turn end with the wingman's raw colour written red - the ordinary
    /// "waiting on you" shape, and the one the old predicate matched on.</summary>
    private static Session RedAtTurnEnd()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);

        // ActivityState defaults to WaitingForInput - a real turn end. IsBrandNew must be cleared or the
        // fold answers "green" (ready) and these would pass for entirely the wrong reason.
        session.IsBrandNew = false;
        session.SetStatusColor("red", "needs you");
        return session;
    }

    // ===== The control. If this breaks, the fix has broken the feature rather than the defect =====

    [Fact]
    public void AnOrdinaryRedSession_IsQueued()
    {
        var session = RedAtTurnEnd();

        var queue = FifoWindow.BuildQueue(new[] { session });

        Assert.Equal(new[] { session.Id }, queue.Select(s => s.Id));
    }

    // ===== THE GAP 2 DEFECT: the fold suppresses a controlled worker's red; the queue ignored it =====

    /// <summary>
    /// A live-controlled Worker must NOT be handed to the owner as "needs you".
    ///
    /// The Gateway resolves this session's role from the whole fleet and stamps it Worker; the fold reads
    /// that and suppresses its red to "supporting" ("Sub-agent" on the rail), because its manager is
    /// dealing with it - the owner is not needed. The rail therefore does not call it red and does not
    /// count it. The old queue predicate never asked: it saw a raw "red" that is not on hold and not
    /// exited, and queued it. So the FIFO handed over a session the rail beside it was calling
    /// "Sub-agent".
    ///
    /// The raw colour is STILL red here, and that assertion is the point: the defect was never that the
    /// colour is wrong, it is that the queue read the raw colour instead of the fold's verdict.
    ///
    /// Watched failing on revert: with the raw-colour predicate restored this session IS queued, so the
    /// queue reads [worker] where it must read [].
    /// </summary>
    [Fact]
    public void ALiveControlledWorker_IsNotQueued_BecauseTheFoldSuppressesItsRed()
    {
        var worker = RedAtTurnEnd();
        worker.ControllerSessionId = Guid.NewGuid();          // controlled - possibly from another machine
        worker.SetGatewayResolvedRole(SessionRoles.Worker);   // ...and the Gateway says so

        // The raw fact the old predicate read is unchanged - it genuinely IS at a turn end, which is
        // exactly why reading the raw colour queued it.
        Assert.Equal("red", worker.StatusColor);
        Assert.False(worker.OnHold);
        // ...and the shared fold says otherwise, in the words the rail beside this window is using.
        Assert.Equal("Sub-agent", new SessionViewModel(worker).ActivityLabel);

        var queue = FifoWindow.BuildQueue(new[] { worker });

        Assert.Empty(queue);
    }

    /// <summary>
    /// The queue and the rail's "N need you" count must agree about the SAME roster, because they now ask
    /// the same function. This is the invariant gap 2 is really about - not "the queue is wrong" but "two
    /// surfaces disagree about one session".
    /// </summary>
    [Fact]
    public void TheQueue_ContainsExactlyWhatTheRailCounts()
    {
        var ordinary = RedAtTurnEnd();
        var worker = RedAtTurnEnd();
        worker.ControllerSessionId = Guid.NewGuid();
        worker.SetGatewayResolvedRole(SessionRoles.Worker);
        var snoozed = RedAtTurnEnd();
        Assert.Equal(Session.HoldOutcome.Held, snoozed.RequestHold(true));
        var roster = new[] { ordinary, worker, snoozed };

        var queue = FifoWindow.BuildQueue(roster);
        var railCounts = roster.Where(s => new SessionViewModel(s).NeedsYou).Select(s => s.Id).ToList();

        Assert.Equal(railCounts, queue.Select(s => s.Id).ToList());
        Assert.Equal(new[] { ordinary.Id }, queue.Select(s => s.Id));
    }

    // ===== The conditions the old predicate hand-rolled. The fold must subsume all three =====

    /// <summary>The fold classifies a held session OnHold, so the removed <c>!s.OnHold</c> clause is not
    /// missed. Green both before and after - a control on the fold, not a proof of the fix.</summary>
    [Fact]
    public void ASnoozedRedSession_IsNotQueued()
    {
        var session = RedAtTurnEnd();
        Assert.Equal(Session.HoldOutcome.Held, session.RequestHold(true));

        Assert.Empty(FifoWindow.BuildQueue(new[] { session }));
    }

    /// <summary>A working session is BLUE - nothing outranks working - so it is never queued, however red
    /// the Director's stale cooked colour still reads. The old predicate had no working check at all: it
    /// would queue this session on the strength of a raw colour the fold has already overruled.</summary>
    [Fact]
    public void AWorkingSession_IsNotQueued_HoweverStaleTheRawColour()
    {
        var session = RedAtTurnEnd();
        session.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Equal("red", session.StatusColor);   // the stale raw fact the old predicate would have read
        Assert.Empty(FifoWindow.BuildQueue(new[] { session }));
    }

    // ===== The queue's ORDER is unchanged - this closed a membership disagreement, not an ordering one ====

    [Fact]
    public void TheQueue_IsOrderedByRepoPathThenId()
    {
        var b = new Session(Guid.NewGuid(), @"C:\b\repo", @"C:\b\repo", null, new InertBackend(), SessionBackendType.ConPty);
        b.IsBrandNew = false; b.SetStatusColor("red", "needs you");
        var a = new Session(Guid.NewGuid(), @"C:\a\repo", @"C:\a\repo", null, new InertBackend(), SessionBackendType.ConPty);
        a.IsBrandNew = false; a.SetStatusColor("red", "needs you");

        var queue = FifoWindow.BuildQueue(new[] { b, a });

        Assert.Equal(new[] { a.Id, b.Id }, queue.Select(s => s.Id));
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process. Mirrors the one
    /// in SessionRailStateTests - same interface, same inert answers.</summary>
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
