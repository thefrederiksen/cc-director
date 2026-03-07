using System.Windows.Media;

namespace CcDirector.Terminal;

public struct TerminalCell
{
    public char Character;
    public Color Foreground;
    public Color Background;
    public bool Bold;
    public bool Italic;
    public bool Underline;
}
