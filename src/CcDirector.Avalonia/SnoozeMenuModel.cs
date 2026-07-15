using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>One row under "Snooze for": the words it shows and the length it sets.</summary>
/// <param name="Header">The words, e.g. "4 hours" or "1 hour  (default)".</param>
/// <param name="Minutes">The length this row snoozes for.</param>
public sealed record SnoozeChoice(string Header, int Minutes);

/// <summary>
/// What the Snooze part of a session's menu (the "..." button on its row) should say, decided from the
/// two facts that drive it: whether the session is already snoozed, and whether this desktop has learned
/// the user's snooze lengths from the Gateway yet.
///
/// Split out from the menu builder so those decisions can be tested without an Avalonia window. The
/// builder does no thinking - it renders this.
/// </summary>
/// <param name="ToggleHeader">The plain top item: "Snooze  (1 hour)", "Snooze", or "Unsnooze".</param>
/// <param name="Choices">
/// The "Snooze for" rows. EMPTY means offer no submenu at all - which happens when this desktop has not
/// learned the lengths yet.
/// </param>
public sealed record SnoozeMenuModel(string ToggleHeader, IReadOnlyList<SnoozeChoice> Choices)
{
    /// <summary>
    /// Decide the menu.
    ///
    /// <paramref name="options"/> is null when this desktop has never successfully read the lengths from
    /// the Gateway. That is not a failure to paper over: the plain Snooze still works (a hold with no
    /// length makes the Gateway apply the user's default), so the item simply does not claim a length it
    /// does not know, and no submenu appears. Inventing a plausible list here would be the one genuinely
    /// bad outcome - it would show lengths that are not the user's.
    ///
    /// The submenu is offered while snoozed too: re-snoozing to a different length in one step is the
    /// point, and beats unsnooze-then-snooze-again.
    /// </summary>
    public static SnoozeMenuModel Build(bool isOnHold, SnoozeOptionsResponse? options)
    {
        if (options is null || options.Presets.Length == 0)
            return new SnoozeMenuModel(isOnHold ? "Unsnooze" : "Snooze", []);

        var toggle = isOnHold
            ? "Unsnooze"
            : $"Snooze  ({SnoozeLengthText.Format(options.DefaultMinutes)})";

        var choices = options.Presets
            .Select(m => new SnoozeChoice(
                m == options.DefaultMinutes
                    ? $"{SnoozeLengthText.Format(m)}  (default)"
                    : SnoozeLengthText.Format(m),
                m))
            .ToList();

        return new SnoozeMenuModel(toggle, choices);
    }
}
