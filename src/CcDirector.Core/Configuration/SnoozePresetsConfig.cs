using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The GATEWAY-OWNED, per-user list of snooze lengths every Snooze menu offers, in whole minutes.
/// Snoozing still has ONE default length - the alarm-clock click, held by
/// <see cref="SnoozeDefaultConfig"/> - and this list is the small set of other lengths a menu can
/// offer beside it. Because every one of the user's devices talks to their one Gateway, this single
/// Gateway setting IS "the same snooze lengths on every device".
///
/// Persisted in <c>config.json</c> as the top-level array key <c>snooze_presets</c>, the same store
/// the other Gateway settings use. The list is deliberately capped at <see cref="MaxPresets"/>: a
/// snooze menu is a glance-and-click surface, not a duration picker, and an unbounded list would
/// make it one.
///
/// The default length is NOT a separate concept here - it is <c>snooze_default_minutes</c>, and the
/// invariant this class enforces is that the default is always one of the lengths in the list. That
/// is why <see cref="Set"/> writes both keys in a single patch: persisting them separately would let
/// a half-applied write leave a default that is not on the menu.
/// </summary>
public static class SnoozePresetsConfig
{
    /// <summary>The config.json top-level key holding the snooze lengths, an array of whole minutes.</summary>
    public const string Key = "snooze_presets";

    /// <summary>
    /// The most lengths the list may hold. Five keeps the Snooze menu readable at a glance; past that
    /// the menu stops being faster than thinking about it.
    /// </summary>
    public const int MaxPresets = 5;

    /// <summary>
    /// The lengths a Gateway offers when the user has never edited the list: a short interruption, one
    /// hour (which is also <see cref="SnoozeDefaultConfig.Default"/>, so the out-of-the-box click is
    /// unchanged), a half day, and a full working day.
    /// </summary>
    public static IReadOnlyList<int> Shipped { get; } = new[] { 15, 60, 240, 480 };

    /// <summary>
    /// Returns the snooze lengths, ascending. When <c>snooze_presets</c> has never been persisted the
    /// list is derived from <see cref="Shipped"/> - plus the user's own default when they had already
    /// set a custom one, so upgrading from the single-length setting never drops the length they chose.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>snooze_presets</c> key is present but is not an array of one to <see cref="MaxPresets"/>
    /// distinct in-range whole minutes, or it does not contain <c>snooze_default_minutes</c>.
    /// </exception>
    public static IReadOnlyList<int> Get()
    {
        var defaultMinutes = SnoozeDefaultConfig.Get();
        var node = CcDirectorConfigService.ReadRaw()[Key];
        if (node is null)
        {
            var derived = Derive(defaultMinutes);
            FileLog.Write($"[SnoozePresetsConfig] Get: no persisted list -> derived [{string.Join(", ", derived)}]");
            return derived;
        }

        if (node is not JsonArray array)
            throw new InvalidOperationException(
                "config.json key 'snooze_presets' must be an array of whole minutes, for example [15, 60, 240, 480]. " +
                "Fix the value or remove the key to use the shipped lengths.");

        var presets = new List<int>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<int>(out var minutes))
                throw new InvalidOperationException(
                    "config.json key 'snooze_presets' must hold only whole numbers of minutes. " +
                    "Fix the value or remove the key to use the shipped lengths.");

            if (!SnoozeDefaultConfig.IsValid(minutes))
                throw new InvalidOperationException(
                    $"config.json key 'snooze_presets' has the out-of-range length {minutes}. Each length must be " +
                    $"between {SnoozeDefaultConfig.MinMinutes} and {SnoozeDefaultConfig.MaxMinutes} minutes.");

            if (presets.Contains(minutes))
                throw new InvalidOperationException(
                    $"config.json key 'snooze_presets' lists {minutes} more than once. Each length may appear only once.");

            presets.Add(minutes);
        }

        if (presets.Count == 0 || presets.Count > MaxPresets)
            throw new InvalidOperationException(
                $"config.json key 'snooze_presets' must hold between 1 and {MaxPresets} lengths, not {presets.Count}.");

        if (!presets.Contains(defaultMinutes))
            throw new InvalidOperationException(
                $"config.json key 'snooze_default_minutes' is {defaultMinutes}, which is not one of 'snooze_presets' " +
                $"[{string.Join(", ", presets)}]. The default snooze length must be one of the offered lengths.");

        presets.Sort();
        FileLog.Write($"[SnoozePresetsConfig] Get: persisted [{string.Join(", ", presets)}], default={defaultMinutes}");
        return presets;
    }

    /// <summary>
    /// The lengths to offer a user who has never edited the list: the shipped set, plus their own
    /// default when they had already moved it off one of the shipped values. Ascending. Pure, and
    /// public because it is the whole upgrade rule from the old single-length setting - the one part
    /// of <see cref="Get"/> worth testing without touching a real config.json.
    /// </summary>
    public static IReadOnlyList<int> Derive(int defaultMinutes)
    {
        var presets = new List<int>(Shipped);
        if (!presets.Contains(defaultMinutes))
            presets.Add(defaultMinutes);
        presets.Sort();
        return presets;
    }

    /// <summary>
    /// True when <paramref name="presets"/> and <paramref name="defaultMinutes"/> are a pair this
    /// setting will accept. Pure, so an endpoint can validate a request body before writing anything;
    /// <paramref name="error"/> carries a message fit to hand straight back to the caller.
    /// </summary>
    public static bool IsValidSet(IReadOnlyList<int>? presets, int defaultMinutes, out string error)
    {
        if (presets is null || presets.Count == 0)
        {
            error = "at least one snooze length is required";
            return false;
        }

        if (presets.Count > MaxPresets)
        {
            error = $"at most {MaxPresets} snooze lengths are allowed, not {presets.Count}";
            return false;
        }

        foreach (var minutes in presets)
        {
            if (!SnoozeDefaultConfig.IsValid(minutes))
            {
                error = $"the snooze length {minutes} must be between {SnoozeDefaultConfig.MinMinutes} "
                        + $"and {SnoozeDefaultConfig.MaxMinutes} minutes";
                return false;
            }
        }

        if (presets.Distinct().Count() != presets.Count)
        {
            error = "each snooze length may appear only once";
            return false;
        }

        if (!presets.Contains(defaultMinutes))
        {
            error = $"the default snooze length {defaultMinutes} must be one of the offered lengths";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The list that results from making <paramref name="defaultMinutes"/> the default of
    /// <paramref name="presets"/>: unchanged when the length is already offered, and the length added
    /// in ascending order when it is not. Pure, so <see cref="SetDefault"/> can be tested without a
    /// real config.json. Returns null when the length is not already offered AND the list is full -
    /// the caller must fail loud rather than guess which length to evict.
    /// </summary>
    public static IReadOnlyList<int>? WithDefault(IReadOnlyList<int> presets, int defaultMinutes)
    {
        if (presets.Contains(defaultMinutes))
            return presets;

        if (presets.Count >= MaxPresets)
            return null;

        var widened = new List<int>(presets) { defaultMinutes };
        widened.Sort();
        return widened;
    }

    /// <summary>
    /// Makes <paramref name="minutes"/> the default snooze length, and ensures it is one of the offered
    /// lengths - a default the menu does not offer would mean one click doing something no row names.
    /// A length already on the menu just becomes the default; one that is not is added to the menu.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minutes"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The length is not offered and the menu is already full, so adding it would exceed
    /// <see cref="MaxPresets"/>. Fail loud: only the user can say which length to drop.
    /// </exception>
    public static void SetDefault(int minutes)
    {
        if (!SnoozeDefaultConfig.IsValid(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes,
                $"snooze default must be between {SnoozeDefaultConfig.MinMinutes} and {SnoozeDefaultConfig.MaxMinutes} minutes");

        var widened = WithDefault(Get(), minutes)
            ?? throw new InvalidOperationException(
                $"{minutes} minutes is not one of your snooze lengths and you already have the maximum of "
                + $"{MaxPresets}. Remove a length first, then make {minutes} the default.");

        Set(widened, minutes);
    }

    /// <summary>
    /// Persists the snooze lengths AND the default that must be one of them, in a single merge patch
    /// so the two can never disagree, and so no other config section is dropped. Lengths are stored
    /// ascending.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The pair fails <see cref="IsValidSet"/>.
    /// </exception>
    public static void Set(IReadOnlyList<int> presets, int defaultMinutes)
    {
        if (!IsValidSet(presets, defaultMinutes, out var error))
            throw new ArgumentException(error, nameof(presets));

        var sorted = presets.OrderBy(m => m).ToList();
        FileLog.Write($"[SnoozePresetsConfig] Set: presets=[{string.Join(", ", sorted)}], default={defaultMinutes}");

        var array = new JsonArray();
        foreach (var minutes in sorted)
            array.Add(minutes);

        CcDirectorConfigService.MergePatch(new JsonObject
        {
            [Key] = array,
            [SnoozeDefaultConfig.Key] = defaultMinutes,
        });
        FileLog.Write("[SnoozePresetsConfig] Set: persisted");
    }
}
