using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class TerminalSubmitTests
{
    [Fact]
    public async Task EchoVerifiedSubmit_EchoingBackend_TypesTextThenSeparateEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.EchoVerifiedSubmitAsync(backend, "hello world", "Test");

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hello world"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_DelayedEcho_WritesEnterOnlyAfterEcho()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Delayed(TimeSpan.FromMilliseconds(120)));

        var submit = TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "hello delayed echo",
            "Test",
            echoTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1));

        await Task.Delay(40);

        Assert.Single(backend.WrittenBytes);
        Assert.Equal(Encoding.UTF8.GetBytes("hello delayed echo"), backend.WrittenBytes[0]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);

        await submit;

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Equal(["hello delayed echo"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_RepaintingPlaceholder_WaitsForRealEchoBeforeEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(
            RecordingEchoStep.RepaintingPlaceholder("thinking placeholder", TimeSpan.FromMilliseconds(80)));

        var submit = TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "hello after repaint",
            "Test",
            echoTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1));

        await Task.Delay(30);

        Assert.Single(backend.WrittenBytes);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);

        await submit;

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Equal(["hello after repaint"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_ScrolledComposerTailEcho_WritesEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        var text = "Do not modify files. Reply OKCODEXSENTDIRECT1A444B4E6BFDB40 only.";
        var visibleTail = TerminalSubmit.NormalizeForEcho(text)[^16..];
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho(visibleTail));

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            text,
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1));

        Assert.True(backend.WrittenBytes.Count > 2);
        Assert.Equal(Encoding.UTF8.GetBytes(text), backend.WrittenBytes.SkipLast(1).SelectMany(b => b).ToArray());
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[^1]);
        Assert.Equal([text], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_ShortPartialTail_DoesNotSubmit()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho("world"));

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "hello world",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1)));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.EnterCount);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_FirstEchoMissing_EscapesAndRetypesBeforeEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.Enqueue(RecordingEchoStep.Withheld());
        backend.EchoScript.Enqueue(RecordingEchoStep.Immediate());

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "retry me",
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("retry me"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("retry me"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[3]);
        Assert.Equal(["retry me"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_EchoNeverAppears_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "never echoed",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1)));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("never echoed"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("never echoed"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[3]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task GenericDriverSubmit_WithheldEcho_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend
        {
            Buffer = new CircularTerminalBuffer(),
        };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "generic prompt",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1)));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task ClaudeDriverSubmit_SlashCorruptedEcho_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.Enqueue(RecordingEchoStep.SlashCorrupted("write this"));
        backend.EchoScript.Enqueue(RecordingEchoStep.SlashCorrupted("write this"));
        var driver = new ClaudeDriver(new EmptyTranscriptReader(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => driver.SubmitAsync(backend, "write this"));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("write this"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("write this"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[3]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_NoBuffer_WritesTextAndEnter()
    {
        var backend = new RecordingSessionBackend(); // Buffer is null

        await TerminalSubmit.EchoVerifiedSubmitAsync(backend, "hi", "Test");

        Assert.Empty(backend.SentTexts);
        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hi"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
    }

    [Fact]
    public void StripAnsi_RemovesCsiSequences()
    {
        var raw = "\x1B[31mred\x1B[0m text";
        Assert.Equal("red text", TerminalSubmit.StripAnsi(raw));
    }

    [Fact]
    public void NormalizeForEcho_KeepsLettersDigitsSlash()
    {
        Assert.Equal("abc123/clear", TerminalSubmit.NormalizeForEcho("a b c 1-2_3 /clear!"));
    }
}
