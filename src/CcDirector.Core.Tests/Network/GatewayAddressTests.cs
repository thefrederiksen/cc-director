using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests.Network;

/// <summary>
/// Tests for <see cref="GatewayAddress"/> - the manual gateway-address entry helper used by the
/// installer connect step and the app settings (issue #1233): computer name plus port, and
/// normalizing a pasted full address.
/// </summary>
public class GatewayAddressTests
{
    [Fact]
    public void TryFromComputerNameAndPort_ValidNameAndPort_BuildsHttpUrl()
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort("SOREN-NORTH", 7878, out var url, out var error);

        Assert.True(ok);
        Assert.Equal("http://SOREN-NORTH:7878", url);
        Assert.Null(error);
    }

    [Fact]
    public void TryFromComputerNameAndPort_TrimsName()
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort("  MACHINE  ", 7878, out var url, out _);

        Assert.True(ok);
        Assert.Equal("http://MACHINE:7878", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryFromComputerNameAndPort_BlankName_Fails(string? name)
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort(name, 7878, out var url, out var error);

        Assert.False(ok);
        Assert.Equal("", url);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("http://machine")]
    [InlineData("machine/path")]
    public void TryFromComputerNameAndPort_NameIsAFullAddress_Fails(string name)
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort(name, 7878, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryFromComputerNameAndPort_NameWithSpace_Fails()
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort("my machine", 7878, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void TryFromComputerNameAndPort_PortOutOfRange_Fails(int port)
    {
        var ok = GatewayAddress.TryFromComputerNameAndPort("MACHINE", port, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryNormalize_BareHostAndPort_AddsHttpScheme()
    {
        var ok = GatewayAddress.TryNormalize("machine:7878", out var url, out var error);

        Assert.True(ok);
        Assert.Equal("http://machine:7878", url);
        Assert.Null(error);
    }

    [Fact]
    public void TryNormalize_FullHttpUrl_TrimsTrailingSlash()
    {
        var ok = GatewayAddress.TryNormalize("http://machine:7878/", out var url, out _);

        Assert.True(ok);
        Assert.Equal("http://machine:7878", url);
    }

    [Fact]
    public void TryNormalize_TailscaleHttpsUrl_IsAccepted()
    {
        var ok = GatewayAddress.TryNormalize("https://soren-north.tail1234.ts.net:7878", out var url, out _);

        Assert.True(ok);
        Assert.Equal("https://soren-north.tail1234.ts.net:7878", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_Blank_Fails(string input)
    {
        var ok = GatewayAddress.TryNormalize(input, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryNormalize_NonHttpScheme_Fails()
    {
        var ok = GatewayAddress.TryNormalize("ftp://machine:21", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
