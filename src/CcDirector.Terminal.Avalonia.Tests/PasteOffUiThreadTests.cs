using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Regression guard for the slow-paste fix. Pasting into the terminal used to type the clipboard one
/// chunk at a time while awaiting back onto the Avalonia UI thread. On a Director hosting many
/// sessions the UI thread is saturated with rendering, so the per-chunk delay continuations sat
/// unscheduled for seconds: a real 92-character claude /login code took 22.6s on this machine, with
/// 11 chunks stalling over 800ms each (one for 7.3s), while the same paste was near-instant on an
/// idle machine. The fix runs the pacing loop on the thread pool (Task.Run + ConfigureAwait(false)),
/// so paste speed no longer depends on UI-thread availability.
///
/// These tests reproduce the exact condition - a fully blocked UI thread - and assert that the real
/// production pacing (<see cref="TerminalControl.HarnessPaceChunksAsync"/>) sails through it, whereas
/// the old naive UI-thread-resuming loop stalls for the whole block. The contrast is the point: if a
/// change makes the production pacing resume on the UI thread again, the first assertion fails.
/// </summary>
public sealed class PasteOffUiThreadTests
{
    // The paste that bit the user: a 92-character single-line code, no newlines.
    private const int PasteLen = 92;

    // How long the "render storm" pins the UI thread. Far longer than the ~180ms a 92-char paste
    // needs when it is not blocked, so the stall - if present - is unmistakable.
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(2500);

    [AvaloniaFact]
    public async Task Production_pacing_does_not_stall_while_ui_thread_is_blocked()
    {
        var terminal = new TerminalControl();
        var text = new string('x', PasteLen);

        var stamps = new ConcurrentQueue<double>();
        var clock = Stopwatch.StartNew();

        // Pin the UI thread for the whole block, then run the REAL pacing. Because it runs on the
        // thread pool, its chunks land during the block instead of waiting for it to end.
        var block = BlockUiThread(BlockDuration);
        await terminal.HarnessPaceChunksAsync(text, _ => stamps.Enqueue(clock.Elapsed.TotalMilliseconds));
        await block;

        var (span, maxGap, count) = Analyze(stamps);

        Assert.True(count > 1, $"expected multiple chunks, got {count}");
        Assert.True(span < 1000,
            $"paste should finish well before the {BlockDuration.TotalMilliseconds}ms UI-thread block; took {span:F0}ms");
        Assert.True(maxGap < 500,
            $"no chunk should stall behind the blocked UI thread; largest gap was {maxGap:F0}ms");
    }

    [AvaloniaFact]
    public async Task Naive_ui_thread_pacing_DOES_stall_while_ui_thread_is_blocked()
    {
        // This is the OLD behavior, inlined: await Task.Delay with no ConfigureAwait(false) and no
        // Task.Run, so every continuation resumes on the (blocked) UI thread. Asserting that THIS
        // stalls proves the block above genuinely saturates the UI thread - otherwise the first
        // test would pass for the wrong reason (a block that never actually bit).
        var text = new string('x', PasteLen);
        var stamps = new ConcurrentQueue<double>();
        var clock = Stopwatch.StartNew();

        var block = BlockUiThread(BlockDuration);
        await NaiveUiThreadPace(text, _ => stamps.Enqueue(clock.Elapsed.TotalMilliseconds));
        await block;

        var (span, _, count) = Analyze(stamps);

        Assert.True(count > 1, $"expected multiple chunks, got {count}");
        Assert.True(span > BlockDuration.TotalMilliseconds - 500,
            $"naive UI-thread pacing should be held off for ~the whole {BlockDuration.TotalMilliseconds}ms block; " +
            $"span was only {span:F0}ms");
    }

    /// <summary>The naive loop the fix replaced: chunked, but resuming on the UI thread.</summary>
    private static async Task NaiveUiThreadPace(string text, Action<byte[]> send)
    {
        const int chunk = 8;
        for (int i = 0; i < text.Length; i += chunk)
        {
            send(System.Text.Encoding.UTF8.GetBytes(text.Substring(i, Math.Min(chunk, text.Length - i))));
            await Task.Delay(15); // no ConfigureAwait(false): continuation returns to the UI thread
        }
    }

    /// <summary>Post a job that spins the UI thread for <paramref name="d"/>, blocking the dispatcher
    /// completely. Returns a task that completes when the block ends.</summary>
    private static Task BlockUiThread(TimeSpan d)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            var until = Stopwatch.GetTimestamp() + (long)(d.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until) { /* pin the UI thread */ }
            tcs.SetResult();
        }, DispatcherPriority.Normal);
        return tcs.Task;
    }

    private static (double span, double maxGap, int count) Analyze(ConcurrentQueue<double> stamps)
    {
        var times = stamps.ToArray();
        Array.Sort(times);
        double maxGap = 0;
        for (int i = 1; i < times.Length; i++)
            maxGap = Math.Max(maxGap, times[i] - times[i - 1]);
        double span = times.Length == 0 ? 0 : times[^1] - times[0];
        return (span, maxGap, times.Length);
    }
}
