using System.Text.RegularExpressions;
using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Locks the contract from issue #1049: the Director main window must fit the display it opens on,
/// down to the supported minimum of a 1280x720 screen - about 1280x672 of work area once the
/// taskbar is subtracted.
///
/// The defect these tests exist for was HEIGHT, not width. The window opened at a fixed 1400x900
/// and on a small display ended up larger than the desktop and inset from the corner, running off
/// the right and bottom edges, which put the Settings button off-screen and unclickable.
/// </summary>
public class WindowFitTests
{
    // What the main window asks for. Kept here so the assertions read as "the preferred size gets
    // shrunk", which is the behaviour under test.
    private const double PreferredWidth = 1400;
    private const double PreferredHeight = 900;

    /// <summary>An unscaled display: physical pixels and device independent pixels are the same.</summary>
    private static WorkArea Unscaled(int width, int height, int x = 0, int y = 0)
        => new(x, y, width, height, 1.0);

    [Fact]
    public void Fit_SupportedMinimumDisplay_WindowFitsTheWorkArea()
    {
        // 1280x720 with a taskbar: the size the owner decision of 30 July 2026 set as the target.
        var area = Unscaled(1280, 672);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.True(placement.Width <= area.LogicalWidth,
            $"width {placement.Width} overflows the {area.LogicalWidth} work area");
        Assert.True(placement.Height <= area.LogicalHeight,
            $"height {placement.Height} overflows the {area.LogicalHeight} work area");
    }

    [Fact]
    public void Fit_SupportedMinimumDisplay_HeightIsShrunkNotJustWidth()
    {
        // The regression that shipped: width was clamped and height was not, so the window still
        // ran off the bottom. Assert the height actually came down from the preferred 900.
        var area = Unscaled(1280, 672);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.Equal(672, placement.Height);
    }

    [Theory]
    // The displays the owner decision named, as work areas with a taskbar subtracted.
    [InlineData(1280, 672)]   // 1280x720  - the supported minimum
    [InlineData(1366, 720)]   // 1366x768  - was 20px too tall before
    [InlineData(1536, 816)]   // 1536x864  - a 1080p laptop at 125% scaling
    [InlineData(1024, 720)]   // 1024x768  - covered as a side effect
    public void Fit_CommonDisplays_WindowIsFullyInsideTheWorkArea(int workWidth, int workHeight)
    {
        var area = Unscaled(workWidth, workHeight);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        // The whole failure was a window whose right and bottom edges left the screen. Assert on
        // the EDGES, not just the size, because a correctly sized window placed at a fixed inset
        // still runs off - that is precisely what happened at 52,52.
        Assert.True(placement.X >= area.X, $"left edge {placement.X} is off-screen");
        Assert.True(placement.Y >= area.Y, $"top edge {placement.Y} is off-screen");
        Assert.True(placement.X + placement.Width <= area.X + area.Width,
            $"right edge {placement.X + placement.Width} runs past the work area at {area.X + area.Width}");
        Assert.True(placement.Y + placement.Height <= area.Y + area.Height,
            $"bottom edge {placement.Y + placement.Height} runs past the work area at {area.Y + area.Height}");
    }

    [Fact]
    public void Fit_ScaledDisplay_UsesLogicalSpaceNotPhysicalPixels()
    {
        // An ordinary 1920x1080 laptop at 150% Windows scaling. The operating system reports a
        // 1920x1032 PHYSICAL work area, but a window only has 1280x688 of device independent space
        // to live in. Reading the physical number as if it were logical makes this display look
        // roomy enough for 1400x900 when it is not - and per the owner decision this is a real
        // population, not a corner case.
        var area = new WorkArea(0, 0, 1920, 1032, 1.5);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.Equal(1280, placement.Width);
        Assert.True(placement.Height <= 688, $"height {placement.Height} overflows 688 logical pixels");
    }

    [Fact]
    public void Fit_ScaledDisplay_PositionStaysInPhysicalPixels()
    {
        // Position is physical while size is logical; mixing the two centres the window wrongly.
        // At 150% a 1280x688 logical window is exactly the 1920x1032 physical work area, so it
        // must land flush at the origin.
        var area = new WorkArea(0, 0, 1920, 1032, 1.5);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.Equal(0, placement.X);
        Assert.Equal(0, placement.Y);
    }

    [Fact]
    public void Fit_LargeDisplay_KeepsThePreferredSize()
    {
        // The fix must not shrink the window on displays that were always fine.
        var area = Unscaled(2560, 1400);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.Equal(PreferredWidth, placement.Width);
        Assert.Equal(PreferredHeight, placement.Height);
    }

    [Fact]
    public void Fit_SecondaryMonitor_CentresWithinThatMonitorsWorkArea()
    {
        // A work area that does not start at the origin - a monitor to the right of the primary.
        // The offset has to be carried through or the window opens on the wrong screen.
        var area = Unscaled(1280, 672, x: 1920, y: 0);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, FrameOverhead.None);

        Assert.Equal(1920, placement.X);
        Assert.True(placement.X + placement.Width <= 1920 + 1280);
    }

    /// <summary>
    /// The window frame overhead measured on Windows 11 with a slot 5 build of the Director on
    /// 30 July 2026: GetWindowRect reported 1416x1071 while GetClientRect reported 1400x1032.
    /// Measured, not assumed - clamping the client alone left the frame 39 pixels over the work
    /// area, which is most of the 68 pixels issue #1049 was over by.
    /// </summary>
    private static readonly FrameOverhead MeasuredWindows11Frame = new(16, 39);

    [Theory]
    [InlineData(1280, 672)]   // 1280x720  - the supported minimum
    [InlineData(1366, 720)]   // 1366x768
    [InlineData(1024, 720)]   // 1024x768
    public void Fit_WithRealFrame_TheFrameFitsTheWorkAreaNotJustTheClient(int workWidth, int workHeight)
    {
        // Avalonia's Width and Height are the CLIENT size, but it is the FRAME the user sees run
        // off the screen. A fit that clamps only the client passes a naive test and still ships the
        // bug, so assert on client PLUS frame.
        var area = Unscaled(workWidth, workHeight);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, MeasuredWindows11Frame);

        var frameWidth = placement.Width + MeasuredWindows11Frame.Width;
        var frameHeight = placement.Height + MeasuredWindows11Frame.Height;

        Assert.True(frameWidth <= area.LogicalWidth,
            $"frame width {frameWidth} overflows the {area.LogicalWidth} work area");
        Assert.True(frameHeight <= area.LogicalHeight,
            $"frame height {frameHeight} overflows the {area.LogicalHeight} work area");
        Assert.True(placement.X + frameWidth <= area.X + area.Width,
            $"frame right edge {placement.X + frameWidth} runs past the work area");
        Assert.True(placement.Y + frameHeight <= area.Y + area.Height,
            $"frame bottom edge {placement.Y + frameHeight} runs past the work area");
    }

    [Fact]
    public void Fit_WithRealFrame_ClientIsShrunkToLeaveRoomForTheTitleBar()
    {
        // The specific arithmetic: on a 672-tall work area the client cannot be 672, it has to be
        // 672 minus the 39-pixel title bar and border.
        var area = Unscaled(1280, 672);

        var placement = WindowFit.Fit(PreferredWidth, PreferredHeight, area, MeasuredWindows11Frame);

        Assert.Equal(672 - 39, placement.Height);
        Assert.Equal(1280 - 16, placement.Width);
    }

    [Fact]
    public void DeclaredMinimumSize_FitsInsideTheSupportedMinimumWorkArea()
    {
        // A minimum LARGER than the smallest supported display cannot be clamped away by Fit -
        // the window manager enforces the minimum and the window overflows anyway. This is the
        // half of the owner's instruction that lives in the markup rather than in the fold, so it
        // is asserted against the markup.
        var axaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "CcDirector.Avalonia", "MainWindow.axaml"));

        var minWidth = DeclaredDouble(axaml, "MinWidth");
        var minHeight = DeclaredDouble(axaml, "MinHeight");

        Assert.True(minWidth <= WindowFit.SupportedMinimumWidth,
            $"MinWidth {minWidth} exceeds the supported minimum display width {WindowFit.SupportedMinimumWidth}");
        Assert.True(minHeight <= WindowFit.SupportedMinimumWorkAreaHeight,
            $"MinHeight {minHeight} exceeds the supported minimum work area height " +
            $"{WindowFit.SupportedMinimumWorkAreaHeight}, so the window cannot fit a 1280x720 screen");
    }

    /// <summary>
    /// Reads an attribute off the Window element itself - the first occurrence in the markup -
    /// rather than any of the many child elements that also carry sizes.
    /// </summary>
    private static double DeclaredDouble(string axaml, string attribute)
    {
        var match = Regex.Match(axaml, attribute + @"=""(?<value>[0-9]+(\.[0-9]+)?)""");
        Assert.True(match.Success, $"MainWindow.axaml declares no {attribute} on the Window element");
        return double.Parse(match.Groups["value"].Value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no 'packages' directory above the test binary).");
    }
}
