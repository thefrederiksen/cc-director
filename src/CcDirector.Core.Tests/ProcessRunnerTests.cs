using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Proves the two process hazards from issue 516 are closed: a child that fills its stderr pipe
/// does not deadlock the capture, and a cancelled run kills the child rather than orphaning it.
/// Uses powershell as a controllable child (Windows-only, as the rest of the suite already is).
/// </summary>
public sealed class ProcessRunnerTests
{
    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): draining stdout to end before stderr lets a child that writes more
    // than the stderr pipe buffer (about 64 KB) block forever - it is stuck writing stderr while
    // the parent is stuck waiting for stdout to end. Both pipes must be drained concurrently. The
    // child here writes half a megabyte to stderr, then a sentinel to stdout; the previous
    // sequential drain would never return.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RunAsync_ChildFloodsStderr_DrainsBothPipes_DoesNotDeadlock()
    {
        const int floodSize = 500_000; // comfortably larger than any pipe buffer
        var script = $"$e = 'x' * {floodSize}; [Console]::Error.Write($e); [Console]::Out.Write('done')";

        var run = ProcessRunner.RunAsync(
            "powershell", new[] { "-NoProfile", "-Command", script }, workingDirectory: null);

        // If the drain regressed to sequential, run never completes and the delay wins.
        //
        // The budget is generous ON PURPOSE, and raising it costs the assertion nothing. What this
        // test distinguishes is "completes" from "NEVER completes": a sequential drain deadlocks
        // permanently, so it fails this test at any timeout whatsoever. The number is only a
        // liveness guard so a regression reports rather than hangs the suite forever.
        //
        // It was 30 seconds, and that is not a drain budget - it also has to cover starting
        // powershell, which is a heavy child process. On a busy continuous-integration runner that
        // start alone can approach it, and the test then fails for machine load rather than for the
        // hazard it exists to catch. Seen twice on 2026-08-19 during a run whose other jobs were
        // saturating the machine; the drain itself takes well under a second locally.
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2)));
        Assert.Same(run, finished);

        var result = await run;
        Assert.True(result.Started);
        Assert.Equal(floodSize, result.StandardError.Length);
        Assert.Equal("done", result.StandardOutput);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): disposing a Process does not terminate the operating-system process.
    // On cancellation the child tree must be killed. The child would write a marker file after a
    // sleep; the run is cancelled during the sleep, and the marker must never appear.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RunAsync_OnCancellation_KillsTheChild_BeforeItCanFinish()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ccd-procrunner-" + Guid.NewGuid().ToString("N") + ".txt");
        var script = $"Start-Sleep -Seconds 4; Set-Content -LiteralPath '{marker}' -Value 'ran'";

        try
        {
            using var cts = new CancellationTokenSource();
            var run = ProcessRunner.RunAsync(
                "powershell", new[] { "-NoProfile", "-Command", script }, workingDirectory: null, cts.Token);

            await Task.Delay(1500); // let powershell start and enter the sleep
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);

            // Wait past the child's own sleep. If it had NOT been killed, it would have written the
            // marker by now.
            await Task.Delay(5000);
            Assert.False(File.Exists(marker), "the child must have been killed before it could write the marker");
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { }
        }
    }
}
