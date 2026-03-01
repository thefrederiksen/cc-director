using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class RelativePathRegexTests
{
    // Uses the actual regex from LinkDetector (no longer duplicated)

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
        var match = LinkDetector.RelativePathRegex.Match(input);

        Assert.True(match.Success, $"Expected '{input}' to match");
        Assert.Equal(input, match.Value);
    }

    // Note: URLs like "https://example.com" are excluded by UrlRegex matching first
    // in LinkDetector, so they are not tested here against RelativePathRegex alone.
    [Theory]
    [InlineData("justAWord")]
    [InlineData("noSeparatorHere")]
    public void RelativePathRegex_DoesNotMatchNonPaths(string input)
    {
        var match = LinkDetector.RelativePathRegex.Match(input);

        Assert.False(match.Success, $"Expected '{input}' NOT to match but got '{match.Value}'");
    }
}
