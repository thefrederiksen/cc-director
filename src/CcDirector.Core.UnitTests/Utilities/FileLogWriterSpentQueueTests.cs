using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// Regression tests for devthrottle_internal#1312: a logging call must never take down its caller.
///
/// Enqueue's documentation promises it never blocks and drops a line it cannot accept. That was true of a
/// FULL queue and false of a COMPLETED one: BlockingCollection.TryAdd returns false when full but THROWS
/// InvalidOperationException once CompleteAdding has run. The throw escaped into whichever caller happened
/// to be logging at the time, so the failure always landed on an innocent bystander - which is why one
/// defect looked like several unrelated flaky tests, each one passing when run alone.
///
/// These live in the PARALLEL assembly on purpose. They construct their own writer and touch no static
/// state, so they need no serialisation - and this assembly is in the default gate, which is where the
/// guarantee that logging cannot throw belongs. The lifetime half of the fix is tested in Core.Tests,
/// where FileLog's static state can be owned by one test at a time.
/// </summary>
public sealed class FileLogWriterSpentQueueTests
{
    private static FileLogWriter NewWriter(string dir)
        => new(dir, "test-instance", () => DateTime.Now);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "filelog-spent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Enqueue_AfterStop_DoesNotThrow_AndCountsTheLineAsDropped()
    {
        var dir = TempDir();
        try
        {
            var writer = NewWriter(dir);
            writer.Start();
            writer.Stop();

            var before = writer.DroppedLines;

            // The whole defect in one line: this threw InvalidOperationException before the fix.
            var ex = Record.Exception(() => writer.Enqueue("a line written after the writer was stopped"));

            Assert.Null(ex);
            Assert.Equal(before + 1, writer.DroppedLines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Enqueue_AfterStop_StaysSilentAcrossManyCalls()
    {
        // One call not throwing could be luck of timing; the failing behaviour was every call, forever,
        // for the life of the process. Assert the count too - "did not throw" alone would also pass if
        // the line were silently swallowed without being recorded as lost.
        var dir = TempDir();
        try
        {
            var writer = NewWriter(dir);
            writer.Start();
            writer.Stop();

            var before = writer.DroppedLines;
            var ex = Record.Exception(() =>
            {
                for (var i = 0; i < 50; i++) writer.Enqueue($"line {i}");
            });

            Assert.Null(ex);
            Assert.Equal(before + 50, writer.DroppedLines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsSpent_IsFalseBeforeStopAndTrueAfter()
    {
        // This is the flag FileLog.Start reads to decide whether to replace the writer, so it has to mean
        // what it says. A writer that reported "not spent" after a stop would send Start straight back to
        // reviving a dead queue.
        var dir = TempDir();
        try
        {
            var writer = NewWriter(dir);
            writer.Start();
            Assert.False(writer.IsSpent);

            writer.Stop();
            Assert.True(writer.IsSpent);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Enqueue_OnARunningWriter_IsStillAccepted_NotCountedAsDropped()
    {
        // The guard against fixing the throw by dropping EVERYTHING. If a healthy queue started counting
        // lines as dropped, both tests above would still pass and logging would be quietly broken.
        var dir = TempDir();
        try
        {
            var writer = NewWriter(dir);
            writer.Start();

            var before = writer.DroppedLines;
            writer.Enqueue("a line written while the writer is running");

            Assert.False(writer.IsSpent);
            Assert.Equal(before, writer.DroppedLines);

            writer.Stop();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
