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
    /// <summary>
    /// What the wingman says when it will not answer because a menu owns the screen. The person is in
    /// voice mode - possibly driving - so this states the situation and the ONE thing that unblocks it,
    /// with no jargon and no option numbers (phase 1 does not read the options aloud).
    /// </summary>
    public const string MenuSpoken =
        "This session is waiting on a menu, and I can't pick an option for you yet. "
        + "Open the session in the Cockpit or on your machine and choose one, then I can carry on from here.";

    /// <summary>The short line for a card or a toast - the same fact as <see cref="MenuSpoken"/>, sized
    /// for a screen rather than a speaker.</summary>
    public const string MenuMessage =
        "This session is waiting on a menu. Open it in the Cockpit or on your machine and pick an option - "
        + "voice can't answer a menu yet.";

    /// <summary>The sentence appended to a turn's spoken narration when the turn ended on a menu, so the
    /// person hears it as the turn is read out instead of discovering it when their reply goes nowhere.</summary>
    public const string MenuNarrationSuffix =
        " Heads up - this session is now waiting on a menu, so you'll need to open it and pick an option; I can't answer that by voice yet.";

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
