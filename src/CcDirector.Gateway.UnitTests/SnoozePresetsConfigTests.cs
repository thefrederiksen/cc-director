using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the per-user list of snooze lengths. Only the pure surface is exercised (the shipped
/// list, the upgrade rule, and the validation), so the tests never read or write the real config.json.
/// </summary>
public sealed class SnoozePresetsConfigTests
{
    [Fact]
    public void Shipped_list_is_the_four_agreed_lengths()
    {
        Assert.Equal(new[] { 15, 60, 240, 480 }, SnoozePresetsConfig.Shipped);
    }

    [Fact]
    public void Shipped_list_contains_the_default_snooze_length()
    {
        // The out-of-the-box click must keep doing exactly what it did before the list existed:
        // hold for one hour. That only holds if the default is one of the shipped lengths.
        Assert.Contains(SnoozeDefaultConfig.Default, SnoozePresetsConfig.Shipped);
    }

    [Fact]
    public void Shipped_list_fits_the_cap_with_room_for_a_custom_default()
    {
        // Derive appends the user's own default when it is not already shipped, so the shipped list
        // must leave at least one slot free or the upgrade would overflow the menu.
        Assert.True(SnoozePresetsConfig.Shipped.Count < SnoozePresetsConfig.MaxPresets);
    }

    [Fact]
    public void Derive_keeps_only_the_shipped_lengths_when_the_default_is_already_shipped()
    {
        Assert.Equal(new[] { 15, 60, 240, 480 }, SnoozePresetsConfig.Derive(60));
    }

    [Fact]
    public void Derive_keeps_a_custom_default_the_user_had_already_chosen()
    {
        // Upgrading from the single-length setting must never silently drop the length they picked.
        Assert.Equal(new[] { 15, 30, 60, 240, 480 }, SnoozePresetsConfig.Derive(30));
    }

    [Fact]
    public void Derive_never_exceeds_the_cap()
    {
        Assert.True(SnoozePresetsConfig.Derive(37).Count <= SnoozePresetsConfig.MaxPresets);
    }

    [Fact]
    public void IsValidSet_accepts_the_shipped_list_with_its_default()
    {
        Assert.True(SnoozePresetsConfig.IsValidSet(SnoozePresetsConfig.Shipped, 60, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void IsValidSet_accepts_a_single_length_that_is_also_the_default()
    {
        Assert.True(SnoozePresetsConfig.IsValidSet(new[] { 5 }, 5, out _));
    }

    [Fact]
    public void IsValidSet_accepts_a_full_five()
    {
        Assert.True(SnoozePresetsConfig.IsValidSet(new[] { 5, 15, 60, 240, 480 }, 15, out _));
    }

    [Fact]
    public void IsValidSet_rejects_an_empty_list()
    {
        // The menu can never have nothing on it, which is why the last row's delete is disabled.
        Assert.False(SnoozePresetsConfig.IsValidSet(Array.Empty<int>(), 60, out var error));
        Assert.Contains("at least one", error);
    }

    [Fact]
    public void IsValidSet_rejects_a_null_list()
    {
        Assert.False(SnoozePresetsConfig.IsValidSet(null, 60, out _));
    }

    [Fact]
    public void IsValidSet_rejects_a_sixth_length()
    {
        Assert.False(SnoozePresetsConfig.IsValidSet(new[] { 5, 15, 60, 240, 480, 720 }, 60, out var error));
        Assert.Contains("at most 5", error);
    }

    [Theory]
    [InlineData(0)]              // zero would defeat "always comes back"
    [InlineData(-5)]             // negative is nonsense
    [InlineData(7 * 24 * 60 + 1)] // past the seven-day ceiling
    public void IsValidSet_rejects_an_out_of_range_length(int minutes)
    {
        Assert.False(SnoozePresetsConfig.IsValidSet(new[] { 60, minutes }, 60, out var error));
        Assert.Contains("must be between", error);
    }

    [Fact]
    public void IsValidSet_rejects_the_same_length_twice()
    {
        Assert.False(SnoozePresetsConfig.IsValidSet(new[] { 60, 240, 60 }, 60, out var error));
        Assert.Contains("only once", error);
    }

    [Fact]
    public void WithDefault_leaves_the_list_alone_when_the_length_is_already_offered()
    {
        Assert.Equal(new[] { 15, 60, 240, 480 }, SnoozePresetsConfig.WithDefault(new[] { 15, 60, 240, 480 }, 60));
    }

    [Fact]
    public void WithDefault_puts_an_unoffered_length_on_the_menu()
    {
        // Setting a default the menu does not offer must widen the menu, never leave one click doing
        // something no row names. This is the path the existing snooze-default endpoint takes.
        Assert.Equal(new[] { 1, 15, 60, 240, 480 }, SnoozePresetsConfig.WithDefault(new[] { 15, 60, 240, 480 }, 1));
    }

    [Fact]
    public void WithDefault_refuses_to_widen_a_full_menu()
    {
        // Null tells the caller to fail loud: only the user can say which of the five to drop.
        Assert.Null(SnoozePresetsConfig.WithDefault(new[] { 5, 15, 60, 240, 480 }, 90));
    }

    [Fact]
    public void WithDefault_accepts_a_full_menu_that_already_offers_the_length()
    {
        Assert.Equal(new[] { 5, 15, 60, 240, 480 }, SnoozePresetsConfig.WithDefault(new[] { 5, 15, 60, 240, 480 }, 5));
    }

    [Fact]
    public void IsValidSet_rejects_a_default_that_is_not_on_the_list()
    {
        // The invariant the whole class exists to hold: you cannot default to a length the menu
        // does not offer, because then one click would do something no menu row names.
        Assert.False(SnoozePresetsConfig.IsValidSet(new[] { 15, 60, 240 }, 90, out var error));
        Assert.Contains("must be one of the offered lengths", error);
    }
}
