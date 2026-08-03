using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the per-user default snooze length setting (Snooze Length mission). Only the pure
/// surface is exercised (the default constant and the range validation), so the tests never read or
/// write the real config.json.
/// </summary>
public sealed class SnoozeDefaultConfigTests
{
    [Fact]
    public void Default_is_one_hour()
    {
        Assert.Equal(60, SnoozeDefaultConfig.Default);
    }

    [Theory]
    [InlineData(1, true)]                 // the shortest useful hold (and the live round-trip value)
    [InlineData(60, true)]                // the default
    [InlineData(7 * 24 * 60, true)]       // the ceiling (7 days)
    [InlineData(0, false)]                // zero would defeat "always comes back"
    [InlineData(-5, false)]               // negative is nonsense
    [InlineData(7 * 24 * 60 + 1, false)]  // past the ceiling
    public void IsValid_accepts_only_a_sane_minute_range(int minutes, bool expected)
    {
        Assert.Equal(expected, SnoozeDefaultConfig.IsValid(minutes));
    }
}
