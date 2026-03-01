using Trisight.Core.Detection;
using Xunit;

namespace Trisight.Core.Tests;

public class BoundingRectTests
{
    // ---------------------------------------------------------------
    // Contains
    // ---------------------------------------------------------------

    [Fact]
    public void Contains_PointInside_ReturnsTrue()
    {
        var rect = new BoundingRect(10, 20, 100, 50);

        Assert.True(rect.Contains(50, 40));
    }

    [Fact]
    public void Contains_PointOutside_ReturnsFalse()
    {
        var rect = new BoundingRect(10, 20, 100, 50);

        // Point to the right of the rect
        Assert.False(rect.Contains(200, 40));
    }

    [Fact]
    public void Contains_PointOnEdge_ReturnsTrue()
    {
        var rect = new BoundingRect(10, 20, 100, 50);

        // Left edge
        Assert.True(rect.Contains(10, 40));
        // Right edge (X + Width = 110)
        Assert.True(rect.Contains(110, 40));
        // Top edge
        Assert.True(rect.Contains(50, 20));
        // Bottom edge (Y + Height = 70)
        Assert.True(rect.Contains(50, 70));
        // Top-left corner
        Assert.True(rect.Contains(10, 20));
        // Bottom-right corner
        Assert.True(rect.Contains(110, 70));
    }

    // ---------------------------------------------------------------
    // IoU (Intersection over Union)
    // ---------------------------------------------------------------

    [Fact]
    public void IoU_IdenticalRects_Returns1()
    {
        var rect = new BoundingRect(10, 20, 100, 50);

        double iou = rect.IoU(rect);

        Assert.Equal(1.0, iou);
    }

    [Fact]
    public void IoU_NonOverlappingRects_Returns0()
    {
        var a = new BoundingRect(0, 0, 50, 50);
        var b = new BoundingRect(200, 200, 50, 50);

        double iou = a.IoU(b);

        Assert.Equal(0.0, iou);
    }

    [Fact]
    public void IoU_PartialOverlap_ReturnsExpectedValue()
    {
        // Rect A: (0,0) 100x100 => area 10000
        // Rect B: (50,50) 100x100 => area 10000
        // Overlap: 50x50 = 2500
        // Union: 10000 + 10000 - 2500 = 17500
        // IoU: 2500 / 17500 = 1/7
        var a = new BoundingRect(0, 0, 100, 100);
        var b = new BoundingRect(50, 50, 100, 100);

        double iou = a.IoU(b);

        Assert.Equal(1.0 / 7.0, iou, precision: 5);
    }

    // ---------------------------------------------------------------
    // CenterDistance
    // ---------------------------------------------------------------

    [Fact]
    public void CenterDistance_TwoRects_ReturnsEuclideanDistance()
    {
        // Rect A center: (50, 50)
        // Rect B center: (150, 150)
        // Distance: sqrt((100)^2 + (100)^2) = sqrt(20000) = 141.421...
        var a = new BoundingRect(0, 0, 100, 100);
        var b = new BoundingRect(100, 100, 100, 100);

        double dist = a.CenterDistance(b);

        Assert.Equal(141.4213, dist, precision: 3);
    }

    [Fact]
    public void CenterDistance_SamePosition_Returns0()
    {
        var a = new BoundingRect(10, 20, 60, 40);
        var b = new BoundingRect(10, 20, 60, 40);

        double dist = a.CenterDistance(b);

        Assert.Equal(0.0, dist);
    }

    // ---------------------------------------------------------------
    // Area
    // ---------------------------------------------------------------

    [Fact]
    public void Area_ReturnsWidthTimesHeight()
    {
        var rect = new BoundingRect(5, 10, 30, 20);

        Assert.Equal(600, rect.Area);
    }

    // ---------------------------------------------------------------
    // CenterX, CenterY
    // ---------------------------------------------------------------

    [Fact]
    public void CenterX_CenterY_ReturnMidpoints()
    {
        // CenterX = X + Width/2 = 10 + 100/2 = 60
        // CenterY = Y + 20 + 50/2 = 45
        var rect = new BoundingRect(10, 20, 100, 50);

        Assert.Equal(60, rect.CenterX);
        Assert.Equal(45, rect.CenterY);
    }
}
