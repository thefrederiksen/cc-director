using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

public sealed class AnsiParserBracketedPasteTests
{
    [Fact]
    public void Parse_DecPrivate2004_TracksBracketedPasteMode()
    {
        var cells = new TerminalCell[20, 5];
        var parser = new AnsiParser(cells, 20, 5, new List<TerminalCell[]>(), 100);

        Assert.False(parser.BracketedPasteEnabled);

        parser.Parse(Encoding.UTF8.GetBytes("\x1b[?2004h"));

        Assert.True(parser.BracketedPasteEnabled);

        parser.Parse(Encoding.UTF8.GetBytes("\x1b[?2004l"));

        Assert.False(parser.BracketedPasteEnabled);
    }
}
