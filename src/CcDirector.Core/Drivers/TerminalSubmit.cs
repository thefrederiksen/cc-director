using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Shared terminal-submit primitives for interactive CLI drivers. The echo-verified submit was
/// proven first on ClaudeDriver and then on CodexDriver: many TUIs repaint a cycling placeholder
/// in their composer, so a blind "type text, wait, one Enter" loses the Enter when driven
/// programmatically (the Enter lands mid-repaint and is swallowed, parking the prompt unsubmitted).
/// The fix is to type the text, wait until the composer echoes it back in the terminal byte stream,
/// then press Enter as a separate keystroke. This class is the single home for that logic so every
/// driver (Codex, Pi, ...) uses one tested implementation.
/// </summary>
public static class TerminalSubmit
{
    private static readonly byte[] EnterByte = [0x0D];
    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] BracketedPasteStart = Encoding.UTF8.GetBytes("\x1b[200~");
    private static readonly byte[] BracketedPasteEnd = Encoding.UTF8.GetBytes("\x1b[201~");

    /// <summary>
    /// The single ConPTY submit protocol: trim caller submit newlines, use bracketed paste for
    /// large or multi-line blocks when the target TUI requested it, fall back to an @-temp-file
    /// reference when needed, and otherwise echo-verify before pressing Enter.
    /// <paramref name="screenSnapshot"/>, when provided, returns the CURRENT rendered screen rows
    /// and is consulted as a second opinion whenever the byte-stream echo check misses (see
    /// <see cref="ScreenShowsText"/>).
    /// </summary>
    public static async Task SharedSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        bool bracketedPasteEnabled = false,
        bool requireEcho = true,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var textForCheck = text.TrimEnd('\r', '\n');
        if (ShouldUseInstructionFile(driverTag, textForCheck)
            && !string.IsNullOrWhiteSpace(backend.WorkingDirectory))
        {
            await SubmitViaInstructionFileAsync(
                backend,
                textForCheck,
                driverTag,
                bracketedPasteEnabled,
                requireEcho,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot);
            return;
        }

        if (LargeInputHandler.IsLargeInput(textForCheck))
        {
            if (bracketedPasteEnabled)
            {
                await BracketedPasteSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay);
                return;
            }

            if (!string.IsNullOrWhiteSpace(backend.WorkingDirectory))
            {
                await SubmitViaAtReferenceAsync(backend, textForCheck, driverTag, echoTimeout, pollInterval, enterSettleDelay, screenSnapshot);
                return;
            }
        }

        if (requireEcho)
        {
            await EchoVerifiedInlineSubmitAsync(
                backend,
                textForCheck,
                driverTag,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay);
        }
    }

    /// <summary>
    /// Type <paramref name="text"/>, wait for the composer to echo it, then press Enter. Falls back
    /// to the backend's blind submit for large/multi-line input (the @-temp-file path) and for
    /// non-buffering backends (nothing to echo-verify against). Throws if the composer never echoes
    /// the typed text after two attempts, rather than silently parking the prompt.
    /// </summary>
    public static async Task EchoVerifiedSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null)
        => await SharedSubmitAsync(
            backend,
            text,
            driverTag,
            bracketedPasteEnabled: false,
            requireEcho: true,
            echoTimeout,
            pollInterval,
            enterSettleDelay,
            screenSnapshot);

    private static async Task EchoVerifiedInlineSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var buffer = backend.Buffer;
        if (buffer is null)
        {
            backend.Write(Encoding.UTF8.GetBytes(text));
            await Task.Delay(enterSettleDelay ?? TimeSpan.FromMilliseconds(50));
            backend.Write(EnterByte);
            return;
        }

        var to = echoTimeout ?? TimeSpan.FromSeconds(4);
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var settle = enterSettleDelay ?? TimeSpan.FromMilliseconds(40);
        var needle = NormalizeForEcho(text);
        var visibleTailNeedle = VisibleTailNeedle(needle);

        var cursor = buffer.TotalBytesWritten;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cursor = buffer.TotalBytesWritten;
            await WriteTextAsync(backend, text);

            if (needle.Length == 0 || await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, to, poll))
            {
                await Task.Delay(settle);
                backend.Write(EnterByte);
                return;
            }

            // The byte stream missed the echo, but that stream is a poor witness for input that
            // WRAPPED across composer rows: the TUI repaints wrapped text interleaved with box
            // borders, footer hints ("esc again to clear") and cursor moves, so the typed text may
            // never appear in the bytes as one contiguous run even though it is sitting in the
            // composer. Ask the rendered screen - the final visual state, free of interleaving -
            // before treating the attempt as a failure and disturbing the composer with Escape.
            if (ScreenShowsText(screenSnapshot, needle, visibleTailNeedle))
            {
                FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: byte-stream echo missed on attempt {attempt} " +
                              $"but the rendered screen shows the typed text (len={text.Length}) - pressing Enter");
                await Task.Delay(settle);
                backend.Write(EnterByte);
                return;
            }

            if (attempt == 2 && driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
                break;

            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen on attempt {attempt} " +
                          $"(len={text.Length}) - clearing the composer and retyping. " +
                          EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle));
            backend.Write(EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        if (driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: OpenCode echo was torn; pressing Enter instead of failing route");
            await Task.Delay(settle);
            backend.Write(EnterByte);
            return;
        }

        throw new ComposerNotAcceptingInputException(
            $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts - " +
            "the TUI is not accepting input (a modal, a picker, or a composer still initializing). " +
            $"{EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle)} " +
            $"Readable buffer tail: {TailOf(buffer)}");
    }

    private static async Task BracketedPasteSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: bracketed paste submit len={text.Length}");
        backend.Write(BracketedPasteStart);
        await WriteTextAsync(backend, text);
        backend.Write(BracketedPasteEnd);
        await Task.Delay(enterSettleDelay ?? TimeSpan.FromMilliseconds(80));
        backend.Write(EnterByte);
    }

    private static async Task TypeSettleEnterSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: type-settle-enter submit len={text.Length}");
        await WriteTextAsync(backend, text);
        await Task.Delay(enterSettleDelay ?? TimeSpan.FromMilliseconds(50));
        backend.Write(EnterByte);
    }

    private static async Task SubmitViaAtReferenceAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null)
    {
        var tempPath = LargeInputHandler.CreateTempFile(text, backend.WorkingDirectory);
        var relRef = LargeInputHandler.MakeAtReference(tempPath, backend.WorkingDirectory);
        var atReference = $"@{relRef}";
        FileLog.Write($"[{driverTag}] SharedSubmit: large input ({text.Length} chars), using temp file reference: {atReference}");

        await EchoVerifiedInlineSubmitAsync(
            backend,
            atReference,
            driverTag,
            echoTimeout,
            pollInterval,
            enterSettleDelay,
            screenSnapshot);

        await AtReferenceSubmitVerifier.EnsureSubmittedAsync(backend.Buffer, backend.Write, atReference);
    }

    private static async Task SubmitViaInstructionFileAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        bool bracketedPasteEnabled,
        bool requireEcho,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null)
    {
        var tempPath = LargeInputHandler.CreateTempFile(text, backend.WorkingDirectory);
        var relRef = LargeInputHandler.MakeAtReference(tempPath, backend.WorkingDirectory);
        var fileName = Path.GetFileName(tempPath);
        var instruction = "Read file " + fileName + " in the .temp directory. Path: " + relRef +
            ". If the path fails, search for " + fileName +
            ". This file was explicitly created as the user-provided message payload for this turn; it is not hidden context. " +
            "Follow the instructions in that file and reply with the requested strings only.";
        FileLog.Write($"[{driverTag}] SharedSubmit: payload file instruction len={text.Length}, file={relRef}");

        if (requireEcho)
        {
            await EchoVerifiedInlineSubmitAsync(
                backend,
                instruction,
                driverTag,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, instruction, driverTag, enterSettleDelay);
        }
    }

    /// <summary>
    /// Second-opinion echo check against the RENDERED screen instead of the raw byte stream. Wrapped
    /// composer rows reconstruct into the original text when the rows are concatenated and normalized
    /// (the wrap points, borders and padding all fall outside <see cref="NormalizeForEcho"/>'s
    /// alphabet), so finding the needle here is proof the composer holds the typed text even when the
    /// byte stream only ever carried it as interleaved fragments. Also accepts the visible-tail
    /// needle for composers that truncate the display of long input.
    /// </summary>
    private static bool ScreenShowsText(Func<string[]>? screenSnapshot, string needle, string? visibleTailNeedle)
    {
        if (screenSnapshot is null || needle.Length == 0)
            return false;

        var hay = NormalizeForEcho(string.Concat(screenSnapshot()));
        if (hay.Contains(needle, StringComparison.Ordinal))
            return true;

        return visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal);
    }

    private static bool ShouldUseInstructionFile(string driverTag, string text)
    {
        if (!LargeInputHandler.IsLargeInput(text) && text.Length <= 300)
            return false;

        return driverTag.Contains("Codex", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Poll the terminal byte stream until the typed text echoes back in the composer.</summary>
    private static async Task<bool> WaitForEchoAsync(
        CircularTerminalBuffer buffer, long cursor, string needle, string? visibleTailNeedle, TimeSpan timeout, TimeSpan poll)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (bytes, _) = buffer.GetWrittenSince(cursor);
            var hay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
            var index = hay.LastIndexOf(needle, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (index > 0 && hay[index - 1] == '/' && !needle.StartsWith('/'))
                {
                    await Task.Delay(poll);
                    continue;
                }

                return true;
            }

            if (visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal))
                return true;

            await Task.Delay(poll);
        }
        return false;
    }

    private static async Task WriteTextAsync(ISessionBackend backend, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        const int bulkThreshold = 48;
        const int chunkSize = 16;
        if (bytes.Length <= bulkThreshold)
        {
            backend.Write(bytes);
            return;
        }

        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            backend.Write(bytes.AsSpan(offset, count).ToArray());
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>
    /// Codex horizontally scrolls its composer for longer inputs, so the terminal byte stream can
    /// repaint only the visible tail. A long fresh tail is still proof that this exact write reached
    /// the composer because the search is scoped to bytes emitted after the write cursor.
    /// </summary>
    private static string? VisibleTailNeedle(string needle)
    {
        const int tailLength = 16;
        return needle.Length > tailLength ? needle[^tailLength..] : null;
    }

    private static string TailOf(CircularTerminalBuffer buffer)
    {
        var text = NormalizeWhitespace(StripAnsi(Encoding.UTF8.GetString(buffer.DumpAll())));
        const int maxChars = 500;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>
    /// Compact evidence for a missed composer echo, for post-mortem troubleshooting of a false
    /// "composer not accepting input" (issue #1493). Reports what the submit was looking for (the
    /// full needle and the visible-tail needle) and what each witness actually held - the raw byte
    /// stream written since this attempt's cursor, and the rendered screen grid - each normalized to
    /// the echo alphabet and tail-bounded. Last time this fired the log carried only the byte tail,
    /// which could not show why the rendered-screen fallback ALSO missed; the screen fields close
    /// that gap. Distinguishes "text present but full needle scrolled off" (HasTail true, HasNeedle
    /// false) from "text genuinely not on the grid" (both false) from "grid empty" (screenLen 0).
    /// </summary>
    private static string EchoMissDiagnostics(
        CircularTerminalBuffer buffer,
        long cursor,
        Func<string[]>? screenSnapshot,
        string needle,
        string? visibleTailNeedle)
    {
        const int tailChars = 400;

        var (bytes, _) = buffer.GetWrittenSince(cursor);
        var byteHay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
        var byteHasNeedle = byteHay.Contains(needle, StringComparison.Ordinal);
        var byteHasTail = visibleTailNeedle is not null && byteHay.Contains(visibleTailNeedle, StringComparison.Ordinal);

        string screenInfo;
        if (screenSnapshot is null)
        {
            screenInfo = "screen=<no snapshot supplied by driver>";
        }
        else
        {
            var rows = screenSnapshot();
            var screenHay = NormalizeForEcho(string.Concat(rows));
            var screenHasNeedle = screenHay.Contains(needle, StringComparison.Ordinal);
            var screenHasTail = visibleTailNeedle is not null && screenHay.Contains(visibleTailNeedle, StringComparison.Ordinal);
            screenInfo =
                $"screenRows={rows.Length}, screenLen={screenHay.Length}, " +
                $"screenHasNeedle={screenHasNeedle}, screenHasTail={screenHasTail}, " +
                $"screenTail=\"{TailBounded(screenHay, tailChars)}\"";
        }

        return
            $"[echo-miss] needleLen={needle.Length}, tailNeedle=\"{visibleTailNeedle}\", " +
            $"byteLen={byteHay.Length}, byteHasNeedle={byteHasNeedle}, byteHasTail={byteHasTail}, " +
            $"byteTail=\"{TailBounded(byteHay, tailChars)}\", {screenInfo}";
    }

    private static string TailBounded(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];

    private static string NormalizeWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasWhitespace)
                    sb.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            sb.Append(c);
            previousWasWhitespace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Drop ANSI escape sequences (CSI / OSC / two-byte) from a terminal chunk.</summary>
    public static string StripAnsi(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\x1B')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= raw.Length) break;
            var kind = raw[i + 1];
            if (kind == '[')
            {
                i += 2;
                while (i < raw.Length && (raw[i] < '\x40' || raw[i] > '\x7E')) i++;
            }
            else if (kind == ']')
            {
                i += 2;
                while (i < raw.Length && raw[i] != '\a' && raw[i] != '\x1B') i++;
                if (i + 1 < raw.Length && raw[i] == '\x1B') i++;
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Letters, digits and '/' only - the comparison alphabet for composer echo checks.</summary>
    public static string NormalizeForEcho(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '/')
                sb.Append(c);
        return sb.ToString();
    }
}
