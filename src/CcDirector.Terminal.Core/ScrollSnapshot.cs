namespace CcDirector.Terminal.Core;

/// <summary>
/// Immutable point-in-time view of the three quantities needed to size the
/// scrollbar correctly. Returned by the terminal control's
/// <c>GetScrollSnapshot()</c> method.
///
/// Consumers must derive Maximum/ViewportSize/Value from one snapshot; mixing
/// values from multiple snapshots (the prior bug) causes the thumb to render
/// with inconsistent dimensions when scrollback is mutating during redraw.
/// </summary>
/// <param name="ScrollbackCount">Number of lines currently in scrollback.</param>
/// <param name="ViewportRows">Number of visible rows on the terminal screen.</param>
/// <param name="ScrollOffset">Lines scrolled up from bottom; 0 means "live".</param>
public readonly record struct ScrollSnapshot(
    int ScrollbackCount,
    int ViewportRows,
    int ScrollOffset);
