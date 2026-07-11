using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class SetupExecutableVersionTests
{
    [Theory]
    [InlineData("1.1.0-rc4+abc1234", "1.1.0-rc4")]
    [InlineData("1.1.0-rc4", "1.1.0-rc4")]
    [InlineData("1.0.7+deadbeef", "1.0.7")]
    [InlineData("1.0.7", "1.0.7")]
    [InlineData("  1.0.7+abc  ", "1.0.7")]
    public void Strip_RemovesSourceLinkMetadata(string input, string expected)
    {
        Assert.Equal(expected, SetupExecutableVersion.Strip(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Strip_EmptyOrNull_ReturnsEmpty(string? input)
    {
        Assert.Equal("", SetupExecutableVersion.Strip(input));
    }
}
