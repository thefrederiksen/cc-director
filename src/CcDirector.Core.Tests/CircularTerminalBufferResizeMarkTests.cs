using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Resize marks record the terminal geometry against byte positions in the ring buffer, so a
/// replay can parse every byte at the width it was originally emitted for (issue #1304).
/// </summary>
public class CircularTerminalBufferResizeMarkTests
{
    [Fact]
    public void GetResizeMarksSince_NoMarksRecorded_ReturnsZeroGeometry()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.Write(new byte[50]);

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(0);

        Assert.Equal(0, startCols);
        Assert.Equal(0, startRows);
        Assert.Empty(marks);
    }

    [Fact]
    public void GetResizeMarksSince_ReturnsGeometryInEffectAndLaterMarks()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.RecordResize(150, 40);          // position 0
        buffer.Write(new byte[100]);
        buffer.RecordResize(129, 40);          // position 100
        buffer.Write(new byte[50]);

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(0);

        Assert.Equal(150, startCols);
        Assert.Equal(40, startRows);
        var mark = Assert.Single(marks);
        Assert.Equal(100, mark.Position);
        Assert.Equal(129, mark.Cols);
        Assert.Equal(40, mark.Rows);
    }

    [Fact]
    public void GetResizeMarksSince_MarkExactlyAtPosition_CountsAsStartGeometry()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.RecordResize(150, 40);          // position 0
        buffer.Write(new byte[100]);
        buffer.RecordResize(129, 40);          // position 100

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(100);

        Assert.Equal(129, startCols);
        Assert.Equal(40, startRows);
        Assert.Empty(marks);
    }

    [Fact]
    public void RecordResize_SameGeometryAsNewestMark_IsCollapsed()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.RecordResize(150, 40);
        buffer.Write(new byte[10]);
        buffer.RecordResize(150, 40);          // duplicate geometry, no information gained

        var (_, _, marks) = buffer.GetResizeMarksSince(-1);

        Assert.Single(marks);
    }

    [Fact]
    public void RecordResize_InvalidGeometry_IsIgnored()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.RecordResize(0, 40);
        buffer.RecordResize(150, -1);

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(0);

        Assert.Equal(0, startCols);
        Assert.Equal(0, startRows);
        Assert.Empty(marks);
    }

    [Fact]
    public void RecordResize_PrunesMarksTheRingRolledPast_KeepingTheBaseline()
    {
        // Capacity 100: after writing 250 bytes the ring starts at position 150.
        var buffer = new CircularTerminalBuffer(100);
        buffer.RecordResize(80, 24);           // position 0   - rolled past, prunable
        buffer.Write(new byte[50]);
        buffer.RecordResize(100, 30);          // position 50  - rolled past, but newest before
                                               //                ring start: the baseline, kept
        buffer.Write(new byte[200]);           // total 250, ring start 150
        buffer.RecordResize(120, 40);          // position 250 - triggers pruning

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(150);

        Assert.Equal(100, startCols);
        Assert.Equal(30, startRows);
        var mark = Assert.Single(marks);
        Assert.Equal(250, mark.Position);
        Assert.Equal(120, mark.Cols);
        Assert.Equal(40, mark.Rows);
    }

    [Fact]
    public void Clear_DropsRecordedMarks()
    {
        var buffer = new CircularTerminalBuffer(1024);
        buffer.RecordResize(150, 40);
        buffer.Write(new byte[10]);

        buffer.Clear();

        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(0);
        Assert.Equal(0, startCols);
        Assert.Equal(0, startRows);
        Assert.Empty(marks);
    }

    [Fact]
    public void RecordResize_AfterDispose_DoesNotThrow()
    {
        var buffer = new CircularTerminalBuffer(64);
        buffer.Dispose();

        var exRecord = Record.Exception(() => buffer.RecordResize(150, 40));
        var exGet = Record.Exception(() => _ = buffer.GetResizeMarksSince(0));

        Assert.Null(exRecord);
        Assert.Null(exGet);
    }
}
