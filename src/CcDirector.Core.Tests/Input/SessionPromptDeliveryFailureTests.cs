using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The delivery boundary (issue internal#811). <see cref="Session.SendTextAsync"/> is the one place in
/// the Director that knows BOTH which session the words were for AND whether they went. Before this, a
/// throw here travelled up as an error string on whichever caller happened to be listening plus a line in
/// a log file - which is how two spoken prompts were lost on 2026-07-15 and nobody noticed for two days.
///
/// Watched failing before the catch existed: without it every assertion below reads zero.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class SessionPromptDeliveryFailureTests
{
    public SessionPromptDeliveryFailureTests() => PromptDeliveryFailures.ResetForTests();

    [Fact]
    public async Task SendTextAsync_WhenTheSubmitThrows_CountsTheLostPromptAgainstTheSession()
    {
        var backend = new ScriptedBackend { Fail = true };
        using var session = NewSession(backend);

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => session.SendTextAsync("the words the owner spoke", SendSource.Delivery));

        var tally = PromptDeliveryFailures.Tally(session.Id);
        Assert.Equal(1, tally.FailedDeliveries);
        Assert.True(tally.Unresolved);
        Assert.Contains("composer never echoed", tally.LastFailureReason);
    }

    [Fact]
    public async Task SendTextAsync_WhenTheSubmitThrows_StillPropagatesTheException()
    {
        // Counting the loss is not the same as handling it. The caller's own error path - the 502 the
        // dictation endpoint returns, the phone's retry - must be completely undisturbed by the ledger.
        var backend = new ScriptedBackend { Fail = true };
        using var session = NewSession(backend);

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => session.SendTextAsync("hello", SendSource.Delivery));

        Assert.Contains("composer never echoed", ex.Message);
    }

    [Fact]
    public async Task SendTextAsync_ThatSucceeds_LeavesNothingToReport()
    {
        var backend = new ScriptedBackend();
        using var session = NewSession(backend);

        await session.SendTextAsync("hello", SendSource.UserInput);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(session.Id));
    }

    [Fact]
    public async Task SendTextAsync_ThatLandsAfterAFailure_ClearsTheAlarmAndKeepsTheCount()
    {
        var backend = new ScriptedBackend { Fail = true };
        using var session = NewSession(backend);
        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => session.SendTextAsync("lost", SendSource.Delivery));

        backend.Fail = false;
        await session.SendTextAsync("this one got through", SendSource.Delivery);

        var tally = PromptDeliveryFailures.Tally(session.Id);
        Assert.False(tally.Unresolved);
        Assert.Equal(1, tally.FailedDeliveries);
    }

    [Fact]
    public async Task Dispose_ForgetsTheSessionsCountersButNotTheFleetHistory()
    {
        var backend = new ScriptedBackend { Fail = true };
        var session = NewSession(backend);
        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => session.SendTextAsync("lost", SendSource.Delivery));
        var id = session.Id;

        session.Dispose();

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(id));
        Assert.Single(PromptDeliveryFailures.Recent());
    }

    private static Session NewSession(ISessionBackend backend)
    {
        // The Embedded restore constructor: it is the one that does NOT take the ConPTY route, so the
        // submit under test is the backend's own SendTextAsync and the test needs no terminal at all.
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        return s;
    }

    /// <summary>A backend whose send either lands or throws the real submit failure, on command.</summary>
    private sealed class ScriptedBackend : ISessionBackend
    {
        public bool Fail { get; set; }

        public int ProcessId => 1;
        public string Status => "Running";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface, never raised here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }

        public Task SendTextAsync(string text)
        {
            if (Fail)
                throw new ComposerNotAcceptingInputException(
                    "[ClaudeDriver] EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts");
            return Task.CompletedTask;
        }

        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
