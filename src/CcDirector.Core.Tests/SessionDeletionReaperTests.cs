using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for self-requested session teardown: a session flags ITSELF (or is flagged) for deletion
/// via the Control API, and the Director's deletion reaper removes it on its next sweep once a grace
/// window has elapsed AND it is not actively Working (option (a): never cut off a final in-flight turn).
/// The flag is set asynchronously to the removal, so the calling process is never yanked mid-request.
/// </summary>
public sealed class SessionDeletionReaperTests
{
    private static Session NewSession(ExitableBackend backend)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        return session;
    }

    [Fact]
    public void MarkForDeletion_sets_the_flag_reason_and_a_winding_down_badge()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);

        session.MarkForDeletion("jobs-auto: nothing to report");

        Assert.True(session.PendingDeletion);
        Assert.NotNull(session.DeletionRequestedAt);
        Assert.Equal("jobs-auto: nothing to report", session.DeletionReason);
        Assert.Equal(StatusColor.Unknown, session.StatusColor); // grey / winding down
        Assert.Contains("Marked for deletion", session.LastStatusReason);
    }

    [Fact]
    public void MarkForDeletion_is_idempotent_and_keeps_the_original_request_time()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);

        session.MarkForDeletion("first");
        var firstAt = session.DeletionRequestedAt;

        session.MarkForDeletion("second");

        Assert.Equal(firstAt, session.DeletionRequestedAt); // grace measured from the FIRST request
        Assert.Equal("second", session.DeletionReason);     // reason refreshed
    }

    [Fact]
    public void CancelDeletion_clears_the_flag()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        session.MarkForDeletion("oops");

        session.CancelDeletion();

        Assert.False(session.PendingDeletion);
        Assert.Null(session.DeletionRequestedAt);
        Assert.Null(session.DeletionReason);
    }

    [Fact]
    public async Task Reaper_removes_a_flagged_idle_session_past_the_grace_window()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);

        Guid? removed = null;
        manager.OnSessionRemoved += s => removed = s.Id;
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        manager.ReapPendingDeletions();

        await WaitUntil(() => manager.GetSession(session.Id) is null);
        Assert.Equal(session.Id, removed);
    }

    [Fact]
    public void Reaper_leaves_a_flagged_session_within_the_grace_window()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 60_000 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);
        manager.AdoptSession(session);

        session.MarkForDeletion("done"); // just now; grace has not elapsed
        manager.ReapPendingDeletions();

        Assert.NotNull(manager.GetSession(session.Id)); // still here
    }

    [Fact]
    public async Task Reaper_waits_out_a_working_session_then_reaps_it_when_idle()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Working); // mid final turn
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        manager.ReapPendingDeletions();
        Assert.NotNull(manager.GetSession(session.Id)); // option (a): not reaped while Working

        session.ApplyTerminalActivityState(ActivityState.Idle); // turn finished
        manager.ReapPendingDeletions();

        await WaitUntil(() => manager.GetSession(session.Id) is null);
    }

    [Fact]
    public async Task Reaper_spares_a_session_whose_deletion_was_cancelled()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        session.CancelDeletion();
        manager.ReapPendingDeletions();

        await Task.Delay(100); // give any (incorrect) async reap a chance to run
        Assert.NotNull(manager.GetSession(session.Id));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>An ISessionBackend whose process exit can be raised on demand.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // StatusChanged is required by the interface but unused here
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
}
