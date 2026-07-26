using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SessionGitStatusMonitor"/> - the Director-side poll that publishes each live
/// session's uncommitted file count onto <see cref="Session.UncommittedCount"/>, which is what puts the
/// number on the wire for the Cockpit roster and the phone.
///
/// The property under test in most of these is the one that is easy to get wrong and impossible to see once
/// it ships: A FAILED PROBE MUST NOT LOOK LIKE A CLEAN TREE (issue 516). A count of 0 and "we could not
/// tell" render identically to a reader that collapses them, so the monitor publishes only counts a probe
/// actually produced, and leaves the last known value alone otherwise.
/// </summary>
public sealed class SessionGitStatusMonitorTests
{
    [Fact]
    public async Task RefreshOnce_PublishesTheCountOntoTheSession()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        Assert.Null(session.UncommittedCount);   // nothing probed yet is UNKNOWN, not zero

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: 7));
        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(1, published);
        Assert.Equal(7, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_CleanTreePublishesZero_WhichIsADifferentAnswerFromUnknown()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: 0));
        await monitor.RefreshOnceAsync();

        // A SUCCESSFUL probe that found nothing is a verified-clean tree, and 0 is the honest report of it.
        // The distinction this test protects is against null (unknown), asserted in the tests below.
        Assert.Equal(0, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_FailedProbeLeavesTheLastKnownCount_NeverAFalseZero()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var succeed = true;
        var monitor = Monitor(manager, probe: _ => succeed
            ? new GitCountResult(Success: true, Count: 12)
            : new GitCountResult(Success: false, Count: 0));

        await monitor.RefreshOnceAsync();
        Assert.Equal(12, session.UncommittedCount);

        // git falls over on the next cycle. The count is now UNKNOWN - and unknown must not overwrite a real
        // measurement with a zero, which every reader downstream would render as "this tree is clean".
        succeed = false;
        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(0, published);
        Assert.Equal(12, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_ProbeThatNeverSucceededLeavesTheCountNull()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: false, Count: 0));
        await monitor.RefreshOnceAsync();

        // Null, not 0. A client renders no badge for null; a 0 here would claim a verified-clean tree we
        // have never actually observed.
        Assert.Null(session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_MissingRepositoryFolderIsNotProbedAndKeepsItsLastCount()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);
        session.UncommittedCount = 3;

        var probed = false;
        var monitor = Monitor(manager,
            probe: _ => { probed = true; return new GitCountResult(Success: true, Count: 99); },
            directoryExists: _ => false);

        await monitor.RefreshOnceAsync();

        // A session whose folder was deleted or moved is unreadable, not clean.
        Assert.False(probed);
        Assert.Equal(3, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_OneThrowingProbeDoesNotStopTheOtherSessions()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var bad = NewSession(@"C:\test\bad");
        using var good = NewSession(@"C:\test\good");
        manager.AdoptSession(bad);
        manager.AdoptSession(good);

        var monitor = Monitor(manager, probe: path => path.EndsWith("bad", StringComparison.Ordinal)
            ? throw new InvalidOperationException("git exploded")
            : new GitCountResult(Success: true, Count: 4));

        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(1, published);
        Assert.Null(bad.UncommittedCount);
        Assert.Equal(4, good.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_RaisesTheChangeEventOnlyWhenTheNumberActuallyMoves()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var raised = 0;
        session.OnUncommittedCountChanged += _ => raised++;

        var count = 5;
        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: count));

        await monitor.RefreshOnceAsync();
        await monitor.RefreshOnceAsync();   // same number - an idle session must push nothing
        Assert.Equal(1, raised);

        count = 6;
        await monitor.RefreshOnceAsync();
        Assert.Equal(2, raised);
    }

    private static SessionGitStatusMonitor Monitor(
        SessionManager manager,
        Func<string, GitCountResult> probe,
        Func<string, bool>? directoryExists = null)
        => new(manager,
            interval: TimeSpan.FromHours(1),          // the loop never runs; the tests drive RefreshOnceAsync
            probe: (path, _) => Task.FromResult(probe(path)),
            directoryExists: directoryExists ?? (_ => true));

    private static Session NewSession(string repoPath = @"C:\test\repo")
        => new(Guid.NewGuid(), repoPath, repoPath, null, new NullBackend(), SessionBackendType.ConPty);

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
