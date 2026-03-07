using System.Windows;

namespace CcDirector.Terminal.Rendering;

/// <summary>
/// Describes a link region for hit-testing and rendering.
/// </summary>
public readonly struct LinkRegionInfo
{
    public readonly Rect Bounds;
    public readonly string Text;
    public readonly TerminalLinkType Type;

    public LinkRegionInfo(Rect bounds, string text, TerminalLinkType type)
    {
        Bounds = bounds;
        Text = text;
        Type = type;
    }
}
