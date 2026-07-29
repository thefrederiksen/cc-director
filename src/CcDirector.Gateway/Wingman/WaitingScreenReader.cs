using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Reads a session's LIVE screen over the tunnel and classifies it with the pure
/// <see cref="WaitingScreenClassifier"/> (issue #2193). This is the ONE place that read-and-classify
/// step lives, so every surface that has to know "is this session sitting on a menu right now?" asks
/// the same question and gets the same answer: the wingman voice endpoint, the prompt front door's
/// menu guard, and the narration generator.
///
/// It costs one tunnel read and NO model call - the classifier is pure. That is what makes it usable
/// on the send path (where a model call would add seconds to every spoken reply) and on the narration
/// path (where it must not add provider cost to a turn).
///
/// Fail-closed is inherited from the classifier: an unreadable, blank, or ambiguous screen resolves to
/// <see cref="WaitingScreenKind.Blocked"/>, never to a menu and never to a typeable composer.
/// </summary>
internal static class WaitingScreenReader
{
    // The two SPOKEN lines this reader used to hold as English constants moved to SpokenPhrases with
    // issue #1009 - the product speaks them, so they exist in every language it speaks. They are
    // SpokenPhrases.WaitingScreenMenu and SpokenPhrases.WaitingScreenMenuNarrationSuffix, and both are
    // reached through a language resolved from the account's tenant.

    /// <summary>The short line for a card or a toast - the same fact as
    /// <see cref="Speech.SpokenPhrases.WaitingScreenMenu"/>, sized for a screen rather than a speaker.
    /// It stays ENGLISH: the mission translates what the product SAYS, not what it DISPLAYS, and every
    /// other label in the product is English.</summary>
    public const string MenuMessage =
        "This session is waiting on a menu. Open it in the Cockpit or on your machine and pick an option - "
        + "voice can't answer a menu yet.";

    /// <summary>
    /// Read the live screen grid for <paramref name="sid"/> and classify it. A read that throws, or that
    /// the owning Director does not answer, is <see cref="WaitingScreenKind.Blocked"/> - unreadable is
    /// never mistaken for either a menu or a composer.
    /// </summary>
    public static async Task<WaitingScreenKind> ClassifyAsync(
        SessionVerbClient route, string sid, CancellationToken ct = default)
    {
        ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: screen-grid read threw ({ex.Message}) - treating as unreadable");
            return WaitingScreenKind.Blocked;
        }
        if (grid is null)
            return WaitingScreenKind.Blocked;

        return WaitingScreenClassifier.Classify(
            grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);
    }

    /// <summary>
    /// True when the live screen is CONFIDENTLY a menu. This - not "not a composer" - is the phase 1 gate:
    /// only a positively recognized menu blocks a voice reply, so a screen the classifier merely cannot
    /// read keeps behaving exactly as it did before the guard existed. Blocking on unreadable too would be
    /// the stricter rule, but it would silently break ordinary voice replies, which is a worse outcome than
    /// the gap it would close.
    /// </summary>
    public static async Task<bool> IsMenuAsync(SessionVerbClient route, string sid, CancellationToken ct = default)
        => await ClassifyAsync(route, sid, ct) == WaitingScreenKind.Menu;

    /// <summary>The wire word for a classified screen: what the phone and the Cockpit read.</summary>
    public static string KindWord(WaitingScreenKind kind) => kind switch
    {
        WaitingScreenKind.Menu => "menu",
        WaitingScreenKind.PlainText => "text",
        _ => "blocked",
    };
}
