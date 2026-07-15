namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of <c>GET /gateway/snooze-presets</c>: the snooze lengths every Snooze menu offers and which one
/// the plain one-click Snooze uses. Shared so the Director can read the Gateway's answer with the same
/// shape the Gateway writes it.
///
/// A Director must ASK the Gateway for this rather than read <c>config.json</c> itself: the setting is
/// per-user and Gateway-owned, and a Director on another machine has a different config file, so reading
/// locally would quietly show lengths that are not the user's.
/// </summary>
public sealed class SnoozeOptionsResponse
{
    /// <summary>The lengths to offer, ascending, in whole minutes. Never empty.</summary>
    public int[] Presets { get; set; } = [];

    /// <summary>
    /// The length the plain Snooze click uses, in whole minutes. Always one of <see cref="Presets"/> -
    /// the Gateway holds that invariant.
    /// </summary>
    public int DefaultMinutes { get; set; }

    /// <summary>The most lengths the list may hold. Informational for the Director.</summary>
    public int MaxPresets { get; set; }
}
