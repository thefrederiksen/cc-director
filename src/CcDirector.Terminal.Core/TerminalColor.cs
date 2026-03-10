namespace CcDirector.Terminal.Core;

/// <summary>
/// Platform-independent RGBA color for terminal rendering.
/// Replaces System.Windows.Media.Color / Avalonia.Media.Color in shared code.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;
    public readonly byte A;

    public TerminalColor(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    public static TerminalColor FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

    public static readonly TerminalColor LightGray = FromRgb(0xD3, 0xD3, 0xD3);

    public bool Equals(TerminalColor other) =>
        R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
