using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The setup wizard sizes itself to the display it opens on (devthrottle_internal issue #1046).
///
/// The issue asked one open question - is the wizard fixed, or does it grow with the desktop - and
/// the answer was that it was FIXED: the markup declared 900x640 and nothing anywhere assigned the
/// window's Width or Height, so the 916x679 frame the clean-machine walk photographed at 1024x768
/// was the size it would have been on any monitor. That makes the clipping a defect at every
/// resolution, not a small-screen one.
///
/// These tests bind the size DECLARED IN THE MARKUP to what WindowFit does with it, which is the
/// join a test on either half alone would miss.
/// </summary>
public class WizardWindowFitTests
{
    /// <summary>
    /// What the window frame costs over the client area on Windows 11, measured on issue #1049. It
    /// is the client that Width and Height set and the FRAME that has to fit the desktop, and the
    /// difference is most of what a window overflows a small screen by.
    /// </summary>
    private static readonly FrameOverhead WindowsFrame = new(16, 39);

    /// <summary>The size the wizard would open at with all the room in the world.</summary>
    private static (double Width, double Height) DeclaredPreference()
    {
        // The headless display is roomier than the wizard asks for, so nothing is clamped and what
        // comes back is the preference as declared. Reading it from a real instance is the point:
        // a number copied into the test could not notice the markup changing underneath it.
        var wizard = new FirstRunWizardDialog(new AgentOptions());
        return (wizard.Width, wizard.Height);
    }

    /// <summary>
    /// The old fixed height, kept here as the thing that has to be beaten rather than as a target.
    /// At 640 the Welcome step showed four and a bit of its five items.
    /// </summary>
    private const double TheOldFixedHeight = 640;

    /// <summary>
    /// THE WIRING. Every other test here calls WindowFit.Fit itself, so deleting the two production
    /// calls in the wizard would leave them all green - the reviewer's point, and it was right. The
    /// headless display is roomier than anything under test, so nothing is ever clamped against it
    /// and the call is unobservable; forcing a small work area makes it observable.
    /// </summary>
    [AvaloniaFact]
    public void TheWizardActuallyFitsItselfToTheDisplayItOpensOn()
    {
        WindowFitter.WorkAreaOverride = new WorkArea(0, 0, 1024, 720, 1.0);
        try
        {
            var wizard = new FirstRunWizardDialog(new AgentOptions());

            Assert.True(
                wizard.Height <= 720,
                $"the wizard opened at {wizard.Height} against 720 of work area - it never fitted itself");
            Assert.True(wizard.Width <= 1024);
        }
        finally
        {
            WindowFitter.WorkAreaOverride = null;
        }
    }

    [AvaloniaFact]
    public void TheWizardAsksForMoreRoomThanTheOldFixedHeight()
    {
        var (_, height) = DeclaredPreference();

        Assert.True(
            height > TheOldFixedHeight,
            $"the wizard still prefers {height}, which is no more than the {TheOldFixedHeight} that clipped its lists");
    }

    /// <summary>
    /// The display the defect was photographed on. The wizard used to take 640 of client height there
    /// and leave the rest of the desktop empty; now it takes what the work area allows.
    /// </summary>
    [AvaloniaFact]
    public void OnTheDisplayTheDefectWasFoundOn_TheWizardUsesTheRoomThatIsThere()
    {
        var (width, height) = DeclaredPreference();
        // 1024x768 with a 48 pixel taskbar.
        var area = new WorkArea(0, 0, 1024, 720, 1.0);

        var placement = WindowFit.Fit(width, height, area, WindowsFrame);

        Assert.True(
            placement.Height > TheOldFixedHeight,
            $"at 1024x768 the wizard would still open only {placement.Height} tall");
        Assert.True(
            placement.Height + WindowsFrame.Height <= area.LogicalHeight,
            "the frame must fit inside the work area, not merely the client area");
    }

    /// <summary>
    /// The SMALLEST display the product supports, settled by the owner on issue #1049. The old fixed
    /// size did not fit it: 640 of client plus a 39 pixel frame is 679, against 672 of work area. So
    /// the wizard ran off the bottom of the minimum supported desktop, which nothing had noticed.
    /// </summary>
    [AvaloniaFact]
    public void OnTheSmallestSupportedDisplay_TheWholeFrameFits()
    {
        var (width, height) = DeclaredPreference();
        var area = new WorkArea(0, 0, 1280, (int)WindowFit.SupportedMinimumWorkAreaHeight, 1.0);

        var placement = WindowFit.Fit(width, height, area, WindowsFrame);

        Assert.True(
            placement.Height + WindowsFrame.Height <= area.LogicalHeight,
            $"the frame is {placement.Height + WindowsFrame.Height} tall against {area.LogicalHeight} of work area");
        Assert.True(placement.Width + WindowsFrame.Width <= area.LogicalWidth);
        // The old fixed size, for contrast: this is what did NOT fit.
        Assert.True(TheOldFixedHeight + WindowsFrame.Height > area.LogicalHeight);
    }

    /// <summary>
    /// On an ordinary desktop the wizard gets exactly what it asked for. Clamping is for displays
    /// that cannot hold the preference; it must not shrink a window that fits.
    /// </summary>
    [AvaloniaFact]
    public void OnAnOrdinaryDesktop_ThePreferredSizeIsKeptWhole()
    {
        var (width, height) = DeclaredPreference();
        // 1920x1080 with a 48 pixel taskbar.
        var area = new WorkArea(0, 0, 1920, 1032, 1.0);

        var placement = WindowFit.Fit(width, height, area, WindowsFrame);

        Assert.Equal(width, placement.Width);
        Assert.Equal(height, placement.Height);
    }

    /// <summary>
    /// A 1080p laptop at 150% Windows scaling reports a 1920x1032 PHYSICAL work area and offers only
    /// 1280x688 of the space a window is actually sized in. Reading the physical number as logical
    /// would make exactly this machine look roomy enough when it is not.
    /// </summary>
    [AvaloniaFact]
    public void OnAScaledDisplay_TheWizardIsFittedToTheLogicalSpace()
    {
        var (width, height) = DeclaredPreference();
        var area = new WorkArea(0, 0, 1920, 1032, 1.5);

        var placement = WindowFit.Fit(width, height, area, WindowsFrame);

        Assert.True(
            placement.Height + WindowsFrame.Height <= area.LogicalHeight,
            $"the frame is {placement.Height + WindowsFrame.Height} tall against {area.LogicalHeight:F0} of logical work area");
    }
}
