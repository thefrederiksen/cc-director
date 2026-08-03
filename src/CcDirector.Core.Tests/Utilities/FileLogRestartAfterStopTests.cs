using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// Regression tests for the LIFETIME half of devthrottle_internal#1312.
///
/// FileLog.Stop() completes the writer's queue, which cannot be undone. FileLog.Start() then set the
/// _started flag back to 1 and started a thread on the SAME spent writer - restarting the flag and not
/// the writer. From that moment every FileLog.Write passed the "if (_started == 0) return" guard and
/// threw from Enqueue into whatever caller happened to be logging. The guard was correct code and saved
/// nothing, because _started had quietly stopped meaning "the writer can accept lines".
///
/// These tests live here rather than in the parallel Core.UnitTests assembly because they manipulate
/// FileLog's PROCESS-WIDE static state; this assembly serialises, so one test owns FileLog at a time.
/// The half of the fix that does not need static state - Enqueue never throwing - is tested in
/// Core.UnitTests, so it runs in the default gate.
/// </summary>
public sealed class FileLogRestartAfterStopTests
{
    [Fact]
    public void Write_AfterStopAndStart_DoesNotThrow()
    {
        // The exact sequence observed in the failing test hosts: a production shutdown path stops the
        // log, something starts it again, and the next unrelated caller to log takes the exception.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();
        FileLog.Start();

        var ex = Record.Exception(() => FileLog.Write("a line written after a stop and a restart"));

        Assert.Null(ex);
    }

    [Fact]
    public void Write_AfterStopAndStart_IsActuallyAccepted_NotSilentlyDropped()
    {
        // "Did not throw" on its own would also pass if Start left a spent writer in place and every
        // line went to the dropped counter instead - broken logging that no longer announces itself,
        // which is worse than the crash it replaced. So assert the line was ACCEPTED.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();
        FileLog.Start();

        var droppedBefore = FileLog.DroppedLines;
        FileLog.Write("a line that must reach the new writer");

        Assert.Equal(droppedBefore, FileLog.DroppedLines);
    }

    [Fact]
    public void RepeatedStopStartCycles_KeepLogging()
    {
        // One cycle could be satisfied by a single replacement; the writer has to stay replaceable, so a
        // process that stops and starts more than once is not left with a spent writer on the second pass.
        using var scope = FileLog.RedirectForTests();

        var ex = Record.Exception(() =>
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                FileLog.Stop();
                FileLog.Start();
                FileLog.Write($"cycle {cycle}");
            }
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Write_WhileStopped_IsIgnoredRatherThanThrowing()
    {
        // The gap between a stop and the next start is a legitimate state, not an error: the guard is
        // supposed to make logging a no-op there. Pinned so a future change cannot "fix" the restart by
        // removing the guard and reintroducing the throw through the other door.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();

        var ex = Record.Exception(() => FileLog.Write("a line written while the log is stopped"));

        Assert.Null(ex);

        FileLog.Start();
    }
}
