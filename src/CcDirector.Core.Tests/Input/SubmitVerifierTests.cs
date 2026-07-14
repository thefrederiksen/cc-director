using System.Text;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The submit watchdog (issue #212, pull request #1513). The Enter that submits a prompt is unreliable
/// (autocomplete popup eats it; the agent's startup window drops Enter keypresses; a repainting
/// composer swallows it) and a parked prompt freezes the terminal byte count - observed live on the
/// 2026-06-06 restore E2E and again on 2026-07-14 from a phone dictation. The watchdog judges by
/// output RHYTHM: dead windows get a nudge, settling windows (the typed text's own echo/popup
/// repaints - which defeated a one-shot cumulative check live) get patience, real streaming ends the
/// watch, and exhausting every beat throws rather than reporting a submit that never happened.
/// </summary>
public sealed class SubmitVerifierTests
{
    private const string Label = "ClaudeCode: Can you please explain how you do coding...";

    /// <summary>Fast beat so the suite stays quick; passed explicitly, no shared state.</summary>
    private static readonly TimeSpan FastBeat = TimeSpan.FromMilliseconds(20);

    private static byte[] Junk(int n) => Encoding.UTF8.GetBytes(new string('x', n));

    [Fact]
    public async Task SubmittedPrompt_StreamsImmediately_NoNudge()
    {
        // Happy path: the first Enter landed; the agent streams its response right away
        // (i.e. AFTER the watchdog captured its baseline).
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        var watch = SubmitVerifier.PressEnterAndVerifyAsync(buffer, b => writes.Add(b), Label, FastBeat);
        buffer.Write(Junk(4096)); // response stream arrives before the first beat
        await watch;

        var submittingEnter = Assert.Single(writes); // the submitting Enter only - no nudge
        Assert.Equal(new byte[] { 0x0D }, submittingEnter);
    }

    [Fact]
    public async Task EchoThenSilence_IsParked_GetsNudgedUntilStreaming()
    {
        // THE live incident shape that defeated the cumulative check: the typed text's own echo +
        // popup repaint land first (settling window - no nudge yet), then the TUI freezes (parked).
        // The nudge on the first DEAD window wakes the agent up.
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        var watch = SubmitVerifier.PressEnterAndVerifyAsync(buffer, b =>
        {
            writes.Add(b);
            // The FIRST Enter is the submitting one and the TUI swallows it - nothing streams. Only
            // the watchdog's nudge (the second Enter) gets through and starts the turn.
            if (writes.Count > 1)
                buffer.Write(Junk(4096));
        }, Label, FastBeat);
        buffer.Write(Junk(700));      // echo + popup repaint: settling, sub-streaming
        await watch;

        // The submitting Enter, then beat 1 = settling (wait), beat 2 = dead (one nudge).
        Assert.Equal(2, writes.Count);
        Assert.All(writes, w => Assert.Equal(new byte[] { 0x0D }, w));
    }

    [Fact]
    public async Task DeadTui_NudgesEveryBeat_ThenThrowsRatherThanClaimingSuccess()
    {
        // The agent's startup window drops Enters entirely: every beat is dead, every beat nudges,
        // and the watchdog ends by THROWING. This is the whole point of the change - returning
        // quietly here is what let a lost Enter report success and park a dictation in the composer.
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        var error = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => SubmitVerifier.PressEnterAndVerifyAsync(buffer, b => writes.Add(b), Label, FastBeat));

        Assert.Equal(SubmitVerifier.MaxAttempts + 1, writes.Count); // submitting Enter + one nudge per dead beat
        Assert.All(writes, w => Assert.Equal(new byte[] { 0x0D }, w));
        Assert.Contains("parked in the composer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Label, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuousSmallActivity_NeverNudges_ButStillThrowsIfTheTurnNeverStarts()
    {
        // A slow-thinking agent animates its spinner (small but steady output, above the dead-window
        // threshold every beat). The watchdog must not fire Enters into a session that is visibly
        // alive. Deterministic: the beat hook writes the spinner repaint synchronously, so the test
        // never races a real-time painter against the beat clock (the old flake - a beat could catch
        // an empty window under CI timer jitter and nudge).
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => SubmitVerifier.PressEnterAndVerifyAsync(
                buffer, b => writes.Add(b), Label, FastBeat,
                beatDelay: _ =>
                {
                    buffer.Write(Junk(100)); // steady spinner: > QuietWindowBytes, < SubmittedGrowthBytes
                    return Task.CompletedTask;
                }));

        var submittingEnter = Assert.Single(writes); // never nudged a visibly-alive session
        Assert.Equal(new byte[] { 0x0D }, submittingEnter);
    }

    [Fact]
    public async Task SpinnerThatBecomesAReply_IsSubmitted_NoNudge()
    {
        // The patient case must still terminate happily: steady spinner repaints for a couple of
        // beats, then the reply floods in. No nudge, no throw.
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();
        var beats = 0;

        await SubmitVerifier.PressEnterAndVerifyAsync(
            buffer, b => writes.Add(b), Label, FastBeat,
            beatDelay: _ =>
            {
                buffer.Write(++beats < 3 ? Junk(100) : Junk(4096));
                return Task.CompletedTask;
            });

        Assert.Single(writes); // the submitting Enter only
    }

    [Fact]
    public async Task NullBuffer_PressesEnterButNeverNudges_BecauseThereIsNoEvidence()
    {
        // No buffer = no evidence either way, and a blind nudge is NOT safe here: the operator
        // hand-types into the composer too, so an unconditional second Enter could submit whatever
        // they were halfway through. Do nothing rather than guess.
        var writes = new List<byte[]>();
        await SubmitVerifier.PressEnterAndVerifyAsync(null, b => writes.Add(b), Label, FastBeat);
        var submittingEnter = Assert.Single(writes); // pressed, but never nudged or verified
        Assert.Equal(new byte[] { 0x0D }, submittingEnter);
    }
}
