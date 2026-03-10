namespace CcDirector.Terminal.Core;

/// <summary>
/// A single cell in the terminal grid. Stores character and style attributes.
/// </summary>
public struct TerminalCell
{
    public char Character;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public bool Bold;
    public bool Italic;
    public bool Underline;
}
