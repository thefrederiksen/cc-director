namespace CcDirector.Terminal.Core;

/// <summary>
/// Platform-independent rectangle for terminal link hit-testing.
/// Replaces System.Windows.Rect / Avalonia.Rect in shared code.
/// </summary>
public readonly struct TerminalRect
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;

    public TerminalRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool Contains(double px, double py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}
