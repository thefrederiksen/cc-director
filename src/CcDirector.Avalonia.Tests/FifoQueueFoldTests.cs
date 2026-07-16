using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The FIFO takeover's queue is built from the GATEWAY'S folded triage stamp - the SAME stamp the rail's
/// "N need you" count renders - so the queue and the count beside it cannot disagree about one session.
///
/// The desktop no longer folds for itself. Both this window and the rail read
/// <c>SessionDto.TriageBucket</c>, stamped down from the Gateway (<c>Session.GatewayTriageBucket</c>), so a
/// session is queued here exactly when the Gateway calls it needs-you - never when a local re-fold, blind to
/// the Gateway-only inputs (dictation, voice, the snooze clock), would have. A session with no stamp is not
/// queued: the "no Gateway, no fold" floor.
///
/// These drive REAL <see cref="Session"/> objects through the REAL production queue builder
/// (FifoWindow.BuildQueue), stamping each with the display state the Gateway would push down.
/// </summary>
public sealed class FifoQueueFoldTests
{
    private static Session AtTurnEnd()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        return session;
    }

    /// <summary>Stamp the display state the Gateway would push down for a needs-you session.</summary>
    private static Session NeedsYou(string repo = @"C:\test\repo")
    {
        var session = new Session(Guid.NewGuid(), repo, repo, null, new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        return session;
    }

    // ===== The control. If this breaks, the fix has broken the feature rather than the defect =====

    [Fact]
    public void AGatewayNeedsYouSession_IsQueued()
    {
        var session = NeedsYou();

        var queue = FifoWindow.BuildQueue(new[] { session });

        Assert.Equal(new[] { session.Id }, queue.Select(s => s.Id));
    }

    // ===== A controlled worker: the Gateway folds it Active ("Sub-agent"), never needs-you =====

    /// <summary>
    /// A live-controlled Worker must NOT be handed to the owner as "needs you". The Gateway resolves the role
    /// from the whole fleet, suppresses the worker's red to "supporting"/"Sub-agent" and buckets it Active -
    /// and stamps that down. The queue reads the stamp, so it never queues a session the rail is calling
    /// "Sub-agent". (Before, this window re-folded and could diverge from the Gateway on the four
    /// Gateway-only inputs.)
    /// </summary>
    [Fact]
    public void AGatewayActiveWorker_IsNotQueued()
    {
        var worker = AtTurnEnd();
        worker.ApplyGatewayDisplayState("supporting", "Sub-agent", "active", null, null, false);

        // The rail beside this window renders the same stamped label.
        Assert.Equal("Sub-agent", new SessionViewModel(worker).ActivityLabel);

        Assert.Empty(FifoWindow.BuildQueue(new[] { worker }));
    }

    /// <summary>
    /// The queue and the rail's "N need you" count contain EXACTLY the same sessions, because they read the
    /// same stamp. This is the invariant: two surfaces never disagree about one session.
    /// </summary>
    [Fact]
    public void TheQueue_ContainsExactlyWhatTheRailCounts()
    {
        var ordinary = NeedsYou();
        var worker = AtTurnEnd();
        worker.ApplyGatewayDisplayState("supporting", "Sub-agent", "active", null, null, false);
        var snoozed = AtTurnEnd();
        snoozed.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);
        var roster = new[] { ordinary, worker, snoozed };

        var queue = FifoWindow.BuildQueue(roster);
        var railCounts = roster.Where(s => new SessionViewModel(s).NeedsYou).Select(s => s.Id).ToList();

        Assert.Equal(railCounts, queue.Select(s => s.Id).ToList());
        Assert.Equal(new[] { ordinary.Id }, queue.Select(s => s.Id));
    }

    // ===== The Gateway's other buckets are not queued =====

    [Fact]
    public void AGatewaySnoozedSession_IsNotQueued()
    {
        var session = AtTurnEnd();
        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);

        Assert.Empty(FifoWindow.BuildQueue(new[] { session }));
    }

    [Fact]
    public void AGatewayWorkingSession_IsNotQueued()
    {
        var session = AtTurnEnd();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);

        Assert.Empty(FifoWindow.BuildQueue(new[] { session }));
    }

    /// <summary>The "no Gateway, no fold" floor: a session no Gateway has stamped is not queued, rather than
    /// being queued from a local guess. Its TriageBucket is null, so it never matches "needsYou".</summary>
    [Fact]
    public void AnUnstampedSession_IsNotQueued()
    {
        var session = AtTurnEnd();   // no ApplyGatewayDisplayState

        Assert.Empty(FifoWindow.BuildQueue(new[] { session }));
    }

    // ===== The queue's ORDER is unchanged (repo path, then id) =====

    [Fact]
    public void TheQueue_IsOrderedByRepoPathThenId()
    {
        var b = NeedsYou(@"C:\b\repo");
        var a = NeedsYou(@"C:\a\repo");

        var queue = FifoWindow.BuildQueue(new[] { b, a });

        Assert.Equal(new[] { a.Id, b.Id }, queue.Select(s => s.Id));
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
