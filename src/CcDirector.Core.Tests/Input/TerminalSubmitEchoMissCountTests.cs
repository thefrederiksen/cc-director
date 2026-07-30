using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// Composer echo misses are COUNTED against the session they happened on (issue internal#811).
///
/// This is the leading indicator nobody could see. On 2026-07-15 there were six echo-miss events and two
/// of them turned into lost prompts; every one of the six existed only as a line in a Director log file.
/// The miss itself raises no alarm - the retype usually works - but a session quietly racking them up is
/// the session about to eat somebody's words.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class TerminalSubmitEchoMissCountTests
{
    private static readonly TimeSpan FastVerifyBeat = TimeSpan.FromMilliseconds(20);

    public TerminalSubmitEchoMissCountTests() => PromptDeliveryFailures.ResetForTests();

    [Fact]
    public async Task ComposerThatNeverEchoes_CountsAMissPerAttemptAgainstTheNamedSession()
    {
        var sessionId = Guid.NewGuid();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.SharedSubmitAsync(
                backend,
                "a dictation the composer refuses to take",
                "ClaudeDriver",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                submitVerifyBeat: FastVerifyBeat,
                sessionId: sessionId));

        var tally = PromptDeliveryFailures.Tally(sessionId);
        // Two attempts, both missed, before the submit gives up and throws.
        Assert.Equal(2, tally.ComposerEchoMisses);
        // The THROW is what the session boundary counts as the lost delivery; this layer only counts
        // misses, so nothing here claims a failed delivery on its own.
        Assert.Equal(0, tally.FailedDeliveries);
        Assert.False(tally.Unresolved);
    }

    [Fact]
    public async Task ComposerThatEchoesFirstTime_CountsNothing()
    {
        var sessionId = Guid.NewGuid();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.SharedSubmitAsync(
            backend, "hello world", "ClaudeDriver", submitVerifyBeat: FastVerifyBeat, sessionId: sessionId);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(sessionId));
    }

    [Fact]
    public async Task SubmitWithNoSessionId_CountsNothingRatherThanInventingAPhantomSession()
    {
        // The driver and backend call sites have no session to name. Their misses must not pile up under
        // one empty id and render as a "session" nobody can open.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.SharedSubmitAsync(
                backend,
                "an unattributed submit",
                "CodexDriver",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                submitVerifyBeat: FastVerifyBeat));

        Assert.Empty(PromptDeliveryFailures.Recent());
    }
}
