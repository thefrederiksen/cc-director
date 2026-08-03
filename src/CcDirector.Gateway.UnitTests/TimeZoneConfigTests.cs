using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Gateway display-time-zone setting. Only the pure surface is exercised (the machine
/// default and the id validation), so the tests never read or write the real config.json.
/// </summary>
public sealed class TimeZoneConfigTests
{
    [Fact]
    public void MachineDefault_is_a_valid_non_empty_zone()
    {
        var id = TimeZoneConfig.MachineDefault();
        Assert.False(string.IsNullOrWhiteSpace(id));
        // Whatever the build host's zone is, the returned id must be one the runtime can resolve.
        Assert.True(TimeZoneConfig.IsValid(id));
    }

    [Theory]
    [InlineData("America/New_York", true)]  // a common IANA id
    [InlineData("Europe/London", true)]
    [InlineData("UTC", true)]               // the runtime resolves "UTC"
    [InlineData("Etc/UTC", true)]
    [InlineData("Not/AZone", false)]        // nonsense id
    [InlineData("", false)]                 // empty
    [InlineData("   ", false)]              // whitespace
    [InlineData(null, false)]               // missing
    public void IsValid_accepts_only_a_resolvable_zone(string? id, bool expected)
    {
        Assert.Equal(expected, TimeZoneConfig.IsValid(id));
    }
}
