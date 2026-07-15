using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// What the right-click Snooze menu says. The interesting cases are the two that a careless change would
/// break: a desktop that has not learned the user's lengths must not invent any, and the length the plain
/// click uses must be the one the menu names.
/// </summary>
public sealed class SnoozeMenuModelTests
{
    private static SnoozeOptionsResponse Shipped() =>
        new() { Presets = [15, 60, 240, 480], DefaultMinutes = 60, MaxPresets = 5 };

    [Fact]
    public void The_plain_item_names_the_length_it_will_use()
    {
        // One click must never be a mystery: the item says what it does.
        Assert.Equal("Snooze  (1 hour)", SnoozeMenuModel.Build(false, Shipped()).ToggleHeader);
    }

    [Fact]
    public void The_plain_item_becomes_Unsnooze_for_a_snoozed_session()
    {
        Assert.Equal("Unsnooze", SnoozeMenuModel.Build(true, Shipped()).ToggleHeader);
    }

    [Fact]
    public void Every_length_is_offered_in_the_submenu_in_the_Gateways_order()
    {
        var model = SnoozeMenuModel.Build(false, Shipped());

        Assert.Equal(new[] { 15, 60, 240, 480 }, model.Choices.Select(c => c.Minutes));
        Assert.Equal(
            new[] { "15 minutes", "1 hour  (default)", "4 hours", "8 hours" },
            model.Choices.Select(c => c.Header));
    }

    [Fact]
    public void The_default_row_is_marked_so_the_submenu_agrees_with_the_plain_item()
    {
        var model = SnoozeMenuModel.Build(false, Shipped());
        var marked = model.Choices.Single(c => c.Header.Contains("(default)"));

        Assert.Equal(60, marked.Minutes);
        // The plain item and the marked row must name the same length, or the menu contradicts itself.
        Assert.Contains("1 hour", model.ToggleHeader);
    }

    [Fact]
    public void A_snoozed_session_still_gets_the_submenu_so_a_length_can_be_changed_in_one_step()
    {
        // Re-snoozing to a different length should not require unsnooze-then-snooze-again.
        Assert.NotEmpty(SnoozeMenuModel.Build(true, Shipped()).Choices);
    }

    [Fact]
    public void Unknown_lengths_offer_a_plain_Snooze_that_claims_no_length()
    {
        // Null options = this desktop has not read the Gateway's lengths yet. The click still works (the
        // Gateway applies the default), so the item must not claim a length it does not know.
        var model = SnoozeMenuModel.Build(false, null);

        Assert.Equal("Snooze", model.ToggleHeader);
        Assert.DoesNotContain("(", model.ToggleHeader);
    }

    [Fact]
    public void Unknown_lengths_offer_no_submenu_rather_than_an_invented_one()
    {
        // The one genuinely bad outcome would be showing plausible lengths that are not the user's.
        Assert.Empty(SnoozeMenuModel.Build(false, null).Choices);
        Assert.Empty(SnoozeMenuModel.Build(true, null).Choices);
    }

    [Fact]
    public void An_empty_list_from_the_Gateway_is_treated_as_unknown_not_as_an_empty_submenu()
    {
        var empty = new SnoozeOptionsResponse { Presets = [], DefaultMinutes = 60, MaxPresets = 5 };
        var model = SnoozeMenuModel.Build(false, empty);

        Assert.Equal("Snooze", model.ToggleHeader);
        Assert.Empty(model.Choices);
    }

    [Fact]
    public void A_single_length_list_marks_it_default_and_still_offers_it()
    {
        var one = new SnoozeOptionsResponse { Presets = [90], DefaultMinutes = 90, MaxPresets = 5 };
        var model = SnoozeMenuModel.Build(false, one);

        Assert.Equal("Snooze  (1 hour 30 minutes)", model.ToggleHeader);
        Assert.Equal("1 hour 30 minutes  (default)", Assert.Single(model.Choices).Header);
    }

    [Fact]
    public void A_custom_default_is_named_by_the_plain_item()
    {
        var custom = new SnoozeOptionsResponse { Presets = [15, 60, 240, 480], DefaultMinutes = 480, MaxPresets = 5 };

        Assert.Equal("Snooze  (8 hours)", SnoozeMenuModel.Build(false, custom).ToggleHeader);
    }
}
