namespace CcDirector.Avalonia;

/// <summary>
/// A monitor's work area - the desktop minus the taskbar - as the operating system reports it,
/// in PHYSICAL pixels, together with the scaling factor that converts those to the device
/// independent pixels Avalonia sizes windows in.
///
/// The distinction matters and is the whole reason this type carries scaling. An ordinary
/// 1920x1080 laptop at 150% Windows scaling reports a 1920x1032 physical work area but only
/// 1280x688 of device independent space, and it is the device independent number a window size
/// has to fit inside. Treating the physical number as if it were logical makes every scaled
/// display look roomier than it is, which is exactly the population this fix is for.
/// </summary>
public readonly record struct WorkArea(int X, int Y, int Width, int Height, double Scaling)
{
    /// <summary>Work area width in device independent pixels.</summary>
    public double LogicalWidth => Width / Scaling;

    /// <summary>Work area height in device independent pixels.</summary>
    public double LogicalHeight => Height / Scaling;
}

/// <summary>
/// How much bigger the window FRAME is than its client area, in device independent pixels - the
/// resize border and the title bar.
///
/// This is the part that is easy to miss and was measured rather than assumed: Avalonia's
/// Window.Width and Window.Height set the CLIENT size, so a client area sized exactly to the work
/// area still produces a frame taller than the desktop. Measured on Windows 11 the overhead is
/// 16 wide and 39 tall, which is most of the 68 pixels issue #1049 was over by.
/// </summary>
public readonly record struct FrameOverhead(double Width, double Height)
{
    public static readonly FrameOverhead None = new(0, 0);
}

/// <summary>
/// Where and how big a window should open: size in device independent pixels (what Avalonia's
/// Width and Height take, i.e. the CLIENT size), position in physical pixels (what Avalonia's
/// Position takes, i.e. the top-left of the FRAME).
/// </summary>
public readonly record struct WindowPlacement(double Width, double Height, int X, int Y);

/// <summary>
/// Decides the opening size and position of the main window so that it always fits the display
/// it opens on.
///
/// The Director used to open at a fixed 1400x900 with no reference to the screen. On a small
/// display that produced a window LARGER than the desktop, inset from the corner, running off the
/// right and bottom edges - which put the Settings button off the right edge and unclickable, and
/// with it every "you can change this later in Settings" promise the setup wizard makes
/// (issue #1049). The supported minimum display is 1280x720, which is about 1280x672 of work area
/// once the taskbar is subtracted.
///
/// Kept free of Avalonia types on purpose so the rule can be tested directly, without a UI thread
/// or a real monitor.
/// </summary>
public static class WindowFit
{
    /// <summary>
    /// The smallest display the Director supports, in device independent pixels. Owner decision of
    /// 30 July 2026 on issue #1049: Microsoft's own Windows 11 requirement is a 720p display, and
    /// 1024x768 does not appear in the desktop resolution statistics at all.
    /// </summary>
    public const double SupportedMinimumWidth = 1280;
    public const double SupportedMinimumHeight = 720;

    /// <summary>
    /// A taskbar's worth of height. Only used to express the supported work area; the real work
    /// area always comes from the operating system.
    /// </summary>
    public const double TypicalTaskbarHeight = 48;

    /// <summary>The work area a window must fit to be usable on the smallest supported display.</summary>
    public const double SupportedMinimumWorkAreaHeight = SupportedMinimumHeight - TypicalTaskbarHeight;

    /// <summary>
    /// Fits a desired window size to a work area and centres it there.
    ///
    /// Size is clamped down to the work area on each axis independently - the defect on issue #1049
    /// was height, not width, and a window can overflow one axis without the other. Centring
    /// replaces the old fixed inset, which was what pushed the window off BOTH the right and bottom
    /// edges rather than just overflowing one of them.
    /// </summary>
    /// <param name="desiredWidth">Preferred CLIENT width in device independent pixels.</param>
    /// <param name="desiredHeight">Preferred CLIENT height in device independent pixels.</param>
    /// <param name="workArea">The work area of the display the window opens on.</param>
    /// <param name="frame">
    /// How much bigger the frame is than the client area. It is the FRAME that has to fit the work
    /// area, so this is subtracted before clamping and added back before centring.
    /// </param>
    public static WindowPlacement Fit(
        double desiredWidth, double desiredHeight, WorkArea workArea, FrameOverhead frame)
    {
        // Clamp the CLIENT size so that client + frame fits the work area.
        var width = Math.Min(desiredWidth, workArea.LogicalWidth - frame.Width);
        var height = Math.Min(desiredHeight, workArea.LogicalHeight - frame.Height);

        // Position is physical and refers to the frame, so centre the FRAME, not the client.
        var physicalFrameWidth = (width + frame.Width) * workArea.Scaling;
        var physicalFrameHeight = (height + frame.Height) * workArea.Scaling;

        var x = workArea.X + (int)Math.Round((workArea.Width - physicalFrameWidth) / 2);
        var y = workArea.Y + (int)Math.Round((workArea.Height - physicalFrameHeight) / 2);

        return new WindowPlacement(width, height, x, y);
    }
}
