using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins the words the desktop Snooze menu uses for a length.
///
/// These strings are duplicated in TypeScript (packages/client-core/src/settings/snoozeFormat.ts) because
/// the desktop is C# and the Cockpit and phone are TypeScript. The expectations below are deliberately the
/// SAME literals that snoozeFormat.test.ts pins, so changing the wording on one side without the other
/// turns this suite red. One Gateway-owned length must read the same everywhere; "4 hours" on the desktop
/// and "240 minutes" on the phone would look like two different settings.
/// </summary>
public sealed class SnoozeLengthTextTests
{
    [Theory]
    // The shipped lengths - these four are what an untouched install offers, so they matter most.
    [InlineData(15, "15 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(240, "4 hours")]
    [InlineData(480, "8 hours")]
    // Singulars.
    [InlineData(1, "1 minute")]
    [InlineData(1440, "1 day")]
    // Sub-hour stays in minutes.
    [InlineData(59, "59 minutes")]
    // Remainders are named, never rounded away.
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(125, "2 hours 5 minutes")]
    [InlineData(2880, "2 days")]
    [InlineData(10080, "7 days")]
    [InlineData(1560, "1 day 2 hours")]
    // Never a zero unit: 1450 must not read "1 day 0 hours".
    [InlineData(1450, "1 day 10 minutes")]
    public void Format_matches_the_words_the_TypeScript_twin_pins(int minutes, string expected)
    {
        Assert.Equal(expected, SnoozeLengthText.Format(minutes));
    }

    [Fact]
    public void Format_names_every_shipped_length_without_saying_minutes_for_the_long_ones()
    {
        // A menu of "15 minutes / 60 minutes / 240 minutes / 480 minutes" would be unreadable at a glance,
        // which is the whole reason this formatter exists.
        var words = SnoozePresetsConfig.Shipped.Select(SnoozeLengthText.Format).ToList();
        Assert.Equal(new[] { "15 minutes", "1 hour", "4 hours", "8 hours" }, words);
    }
}
