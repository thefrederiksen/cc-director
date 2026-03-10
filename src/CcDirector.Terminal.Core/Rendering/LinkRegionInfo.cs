namespace CcDirector.Terminal.Core.Rendering;

/// <summary>
/// Describes a link region for hit-testing and rendering.
/// Uses platform-independent TerminalRect.
/// </summary>
public readonly struct LinkRegionInfo
{
    public readonly TerminalRect Bounds;
    public readonly string Text;
    public readonly TerminalLinkType Type;

    public LinkRegionInfo(TerminalRect bounds, string text, TerminalLinkType type)
    {
        Bounds = bounds;
        Text = text;
        Type = type;
    }
}
