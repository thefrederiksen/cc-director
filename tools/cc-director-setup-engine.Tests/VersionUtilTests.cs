using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class VersionUtilTests
{
    [Theory]
    [InlineData("v0.3.3", "0.3.3")]
    [InlineData("0.3.3", "0.3.3")]
    [InlineData("0.3.3-rc1", "0.3.3")]
    [InlineData("1.2.0.4", "1.2.0")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("0.4.0+build7", "0.4.0")]
    public void TryParse_NormalizesKnownForms(string input, string expected)
    {
        var parsed = VersionUtil.TryParse(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void TryParse_ReturnsNullForGarbage(string? input)
    {
        Assert.Null(VersionUtil.TryParse(input));
    }

    [Theory]
    [InlineData("0.4.0", "0.3.9", true)]
    [InlineData("0.3.9", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("1.0.0", "0.9.99", true)]
    public void IsNewer_ComparesCorrectly(string candidate, string installed, bool expected)
    {
        Assert.Equal(expected, VersionUtil.IsNewer(candidate, installed));
    }

    [Fact]
    public void IsNewer_FalseWhenEitherUnparseable()
    {
        Assert.False(VersionUtil.IsNewer("0.4.0", null));
        Assert.False(VersionUtil.IsNewer(null, "0.3.0"));
        Assert.False(VersionUtil.IsNewer("garbage", "0.3.0"));
    }

    [Theory]
    [InlineData("1.1.0-rc4", true)]
    [InlineData("v1.1.0-rc4", true)]
    [InlineData("1.1.0-rc4+abc123", true)]
    [InlineData("1.1.0", false)]
    [InlineData("v1.1.0", false)]
    [InlineData("1.1.0+abc123", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void HasPreReleaseSuffix_DetectsSuffixAfterStrippingDecoration(string? input, bool expected)
    {
        Assert.Equal(expected, VersionUtil.HasPreReleaseSuffix(input));
    }

    [Theory]
    [InlineData("v1.1.0-rc4", "1.1.0-rc4")]
    [InlineData("1.1.0-rc4", "1.1.0-rc4")]
    [InlineData("1.1.0-RC4+abc123", "1.1.0-rc4")]
    [InlineData("V1.0.7", "1.0.7")]
    [InlineData("1.0.7", "1.0.7")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void CanonicalTag_KeepsSuffixDropsDecorationAndLowercases(string? input, string expected)
    {
        Assert.Equal(expected, VersionUtil.CanonicalTag(input));
    }

    [Fact]
    public void CanonicalTag_PreReleaseAndStableDoNotCollide()
    {
        // The pre-release suffix must survive so an rc build never compares equal to its stable line.
        Assert.NotEqual(VersionUtil.CanonicalTag("1.1.0-rc4"), VersionUtil.CanonicalTag("1.1.0"));
        Assert.Equal(VersionUtil.CanonicalTag("v1.1.0-rc4"), VersionUtil.CanonicalTag("1.1.0-rc4"));
    }
}
