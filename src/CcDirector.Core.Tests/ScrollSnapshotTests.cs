using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for the derivation of scrollbar thumb metrics from a ScrollSnapshot.
/// Covers the conditions that previously mis-sized the Avalonia ScrollBar:
/// zero scrollback, small scrollback, fully scrolled, mid-scroll, resize,
/// and scrollback-at-max.
///
/// The actual UI binding lives in MainWindow.UpdateScrollBar(); this test
/// replicates the arithmetic so it's verifiable without instantiating
/// Avalonia controls.
/// </summary>
public class ScrollSnapshotTests
{
    private static (double maximum, double viewportSize, double value)
        Derive(ScrollSnapshot snap)
    {
        double maximum = Math.Max(snap.ScrollbackCount, 1);
        double viewportSize = snap.ViewportRows;
        double value = maximum - snap.ScrollOffset;
        return (maximum, viewportSize, value);
    }

    [Fact]
    public void EmptyScrollback_ThumbFillsTrack()
    {
        var (max, vp, val) = Derive(new ScrollSnapshot(ScrollbackCount: 0, ViewportRows: 30, ScrollOffset: 0));
        Assert.Equal(1, max);                    // floored
        Assert.Equal(30, vp);                    // ViewportSize >= Maximum => thumb fills
        Assert.Equal(1, val);                    // at bottom (Value == Maximum)
    }

    [Fact]
    public void GrowingScrollback_ThumbShrinks()
    {
        var (max10, vp10, val10) = Derive(new ScrollSnapshot(10, 30, 0));
        var (max100, vp100, val100) = Derive(new ScrollSnapshot(100, 30, 0));
        var (max1000, vp1000, val1000) = Derive(new ScrollSnapshot(1000, 30, 0));

        Assert.Equal(10, max10);   Assert.Equal(30, vp10);   Assert.Equal(10, val10);
        Assert.Equal(100, max100); Assert.Equal(30, vp100);  Assert.Equal(100, val100);
        Assert.Equal(1000, max1000); Assert.Equal(30, vp1000); Assert.Equal(1000, val1000);
    }

    [Fact]
    public void ScrolledUpMid_ValueReflectsPosition()
    {
        var (max, _, val) = Derive(new ScrollSnapshot(100, 30, 50));
        Assert.Equal(100, max);
        Assert.Equal(50, val); // Maximum - offset = 100 - 50
    }

    [Fact]
    public void ScrolledToTop_ValueIsZero()
    {
        var (max, _, val) = Derive(new ScrollSnapshot(100, 30, 100));
        Assert.Equal(100, max);
        Assert.Equal(0, val);
    }

    [Fact]
    public void SnapshotIsAtomic_AllFieldsFromSameInstant()
    {
        // The bug before this refactor: three independent property reads
        // (ScrollbackCount, ViewportRows, ScrollOffset) could see inconsistent
        // values if scrollback grew mid-call. The record-struct snapshot makes
        // that impossible -- fields are baked in at construction.
        var snap = new ScrollSnapshot(100, 30, 25);
        Assert.Equal(100, snap.ScrollbackCount);
        Assert.Equal(30,  snap.ViewportRows);
        Assert.Equal(25,  snap.ScrollOffset);

        // Equality is by value -- safe to compare two snapshots.
        var same = new ScrollSnapshot(100, 30, 25);
        Assert.Equal(snap, same);
    }

    [Fact]
    public void ViewportResize_ThumbSizeFollows()
    {
        var before = Derive(new ScrollSnapshot(200, 30, 0));
        var after = Derive(new ScrollSnapshot(200, 50, 0));

        Assert.Equal(30, before.viewportSize);
        Assert.Equal(50, after.viewportSize);
        Assert.Equal(200, before.maximum);
        Assert.Equal(200, after.maximum);
    }

    [Fact]
    public void OffsetClampedInSnapshot_NotInDerivation()
    {
        // ScrollOffset is clamped by TerminalControl before being exposed;
        // the derivation trusts the snapshot's value. A pathological
        // out-of-range offset still produces a computable (if odd) Value
        // rather than crashing the UI.
        var (max, _, val) = Derive(new ScrollSnapshot(10, 30, 999));
        Assert.Equal(10, max);
        Assert.Equal(-989, val); // intentionally exposes the contract: clamp upstream.
    }
}
