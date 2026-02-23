using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

public class RelativePathRegexTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    // Same pattern used in TerminalControl.cs
    private static readonly Regex RelativePathRegex = new(
        @"\.{0,2}[/\\][^\s""'<>|*?:()\[\]]+|[A-Za-z_][A-Za-z0-9_\-]*[/\\][^\s""'<>|*?:()\[\]]+",
        RegexOptions.Compiled,
        RegexTimeout);

    [Theory]
    [InlineData("tools/communication_manager/run.bat")]
    [InlineData("tools\\communication_manager\\run.bat")]
    [InlineData("./src/file.cs")]
    [InlineData(".\\src\\file.cs")]
    [InlineData("../other/file.txt")]
    [InlineData("..\\other\\file.txt")]
    [InlineData("src/Components/App.tsx")]
    [InlineData("src\\Components\\App.tsx")]
    public void RelativePathRegex_MatchesValidPaths(string input)
    {
        var match = RelativePathRegex.Match(input);

        Assert.True(match.Success, $"Expected '{input}' to match");
        Assert.Equal(input, match.Value);
    }

    // Note: URLs like "https://example.com" are excluded by UrlRegex matching first
    // in TerminalControl, so they are not tested here against RelativePathRegex alone.
    [Theory]
    [InlineData("justAWord")]
    [InlineData("noSeparatorHere")]
    public void RelativePathRegex_DoesNotMatchNonPaths(string input)
    {
        var match = RelativePathRegex.Match(input);

        Assert.False(match.Success, $"Expected '{input}' NOT to match but got '{match.Value}'");
    }
}
