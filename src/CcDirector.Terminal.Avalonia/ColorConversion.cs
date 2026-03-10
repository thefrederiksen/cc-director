using Avalonia.Media;
using CcDirector.Terminal.Core;

namespace CcDirector.Terminal.Avalonia;

/// <summary>
/// Extension methods to convert between platform-independent TerminalColor
/// and Avalonia.Media.Color.
/// </summary>
internal static class ColorConversion
{
    internal static Color ToAvalonia(this TerminalColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static TerminalColor ToTerminal(this Color c) => new(c.R, c.G, c.B, c.A);
}
