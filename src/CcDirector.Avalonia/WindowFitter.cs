using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Applies <see cref="WindowFit"/> to a real Avalonia window: reads the display it is on, clamps the
/// window to that work area, and centres it there.
///
/// This is the Avalonia-touching half of the fix made for the main window on issue #1049, lifted out
/// of MainWindow so the setup wizard can use THE SAME code rather than a second copy of it
/// (devthrottle_internal issue #1046 - the wizard had the identical defect, a size declared in
/// markup with no reference to the display). Two copies of a placement rule drift, and the three
/// things this has to get right were each measured rather than assumed; they are not worth
/// rediscovering a second time.
///
/// The pure decision stays in <see cref="WindowFit"/>, which is tested without a UI thread or a
/// monitor. This class is only the plumbing to and from it.
/// </summary>
internal static class WindowFitter
{
    /// <summary>
    /// Fit before the window is shown. The platform frame does not exist yet, so the border and
    /// title bar overhead is unknown and the size is clamped against the work area alone - enough to
    /// stop a window very much larger than the desktop ever existing. Position is left alone,
    /// because a Position set before Show is discarded.
    /// </summary>
    public static void FitBeforeShow(Window window, string logPrefix)
        => Apply(window, FrameOverhead.None, movePosition: false, stage: "pre-show", logPrefix);

    /// <summary>
    /// Fit again once the window exists, when two things are knowable that were not before: how much
    /// bigger the frame is than the client area, and which display the window actually opened on.
    /// Both were measured to matter on issue #1049 - the frame is 39 device independent pixels taller
    /// than the client on Windows 11.
    /// </summary>
    public static void FitOnOpened(Window window, string logPrefix)
    {
        var frame = window.FrameSize is { } size
            ? new FrameOverhead(
                Math.Max(0, size.Width - window.ClientSize.Width),
                Math.Max(0, size.Height - window.ClientSize.Height))
            : FrameOverhead.None;

        Apply(window, frame, movePosition: true, stage: "opened", logPrefix);
    }

    private static void Apply(Window window, FrameOverhead frame, bool movePosition, string stage, string logPrefix)
    {
        var screens = window.Screens;
        var screen = screens?.ScreenFromWindow(window) ?? screens?.Primary ?? screens?.All.FirstOrDefault();
        if (screen is null)
        {
            // Desktop Avalonia always reports at least one display. Nothing to fit against means
            // something is wrong with the windowing platform, so say so loudly rather than silently
            // opening at a size that may not fit.
            FileLog.Write($"[{logPrefix}] ApplyFit ({stage}) FAILED: the windowing platform reported no display; leaving the window as declared");
            return;
        }

        var area = new WorkArea(
            screen.WorkingArea.X,
            screen.WorkingArea.Y,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height,
            screen.Scaling);

        var placement = WindowFit.Fit(window.Width, window.Height, area, frame);

        FileLog.Write(
            $"[{logPrefix}] ApplyFit ({stage}): workArea={area.Width}x{area.Height} physical, scaling={area.Scaling}, " +
            $"logical={area.LogicalWidth:F0}x{area.LogicalHeight:F0}, frame={frame.Width:F0}x{frame.Height:F0}, " +
            $"desired={window.Width}x{window.Height}, chosen={placement.Width:F0}x{placement.Height:F0} at {placement.X},{placement.Y}");

        window.Width = placement.Width;
        window.Height = placement.Height;

        if (!movePosition)
            return;

        // The platform applies its own default placement as part of showing the window, and that
        // happens AFTER OnOpened - measured on 30 July 2026, a Position assigned there was honoured
        // when the size also changed and silently discarded when it did not, landing the window on a
        // different monitor. Posting the move puts it after the show completes, so it holds either
        // way. Centring is part of the fix, not decoration: a window as tall as the work area is
        // pushed off the bottom by any default offset at all.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        Dispatcher.UIThread.Post(
            () =>
            {
                window.Position = new PixelPoint(placement.X, placement.Y);
                FileLog.Write($"[{logPrefix}] ApplyFit ({stage}): position applied at {window.Position.X},{window.Position.Y}");
            },
            DispatcherPriority.Loaded);
    }
}
