using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// THE desktop palette: one fold-colour NAME, one hex, everywhere. This is the whole of defect 18.
///
/// The colour NAMES are the shared fold's (SessionOrdering.EffectiveColor). The hexes are the
/// Tailwind 500 ramp, and they are written down in docs/new_architecture/session-state.html, which
/// is the single source both this table and the web/mobile client
/// (packages/client-core/src/sessions/ordering.ts) cite. Change the spec's table and both sides in
/// the same pull request, or they drift - which is exactly how this defect happened.
///
/// There used to be FIVE private palettes for the same colour names: the rail said red was #EF4444,
/// the turn review said #E5484D, the (dead) Director view said #F44747 - and there it meant EXITED,
/// not needs-you - the FIFO window said #F44747, and the web client said #F14C4C. Every one of them
/// was a hand-rolled switch beside the code that used it. Nothing tested any of them, so nothing
/// noticed that "the same colour" meant five different pixels.
///
/// TWO deliberate departures from the 500 ramp, both load-bearing:
///   - Error is red-700, not red-500. A session that DIED must never read as one that finished
///     (issue #959).
///   - Grey is ONE grey. Snoozed, exited, and indeterminate all render it, because the fold folds
///     them to one "grey" string on purpose, so clients render them identically. Whether snoozed
///     deserves its own dot colour is an open product question; if the answer is ever yes, it must
///     arrive as a distinct NAME from the fold, never as a client re-reading a raw flag.
/// </summary>
public static class StatusPalette
{
    public const string Red        = "#EF4444";  // red-500      - needs you
    public const string Blue       = "#3B82F6";  // blue-500     - working
    public const string Green      = "#22C55E";  // green-500    - ready (brand new)
    public const string Yellow     = "#EAB308";  // yellow-500   - wingman reading / preparing voice
    public const string Orange     = "#F97316";  // orange-500   - dictation in flight / deep dive
    public const string Purple     = "#A855F7";  // purple-500   - parked on its own background task
    public const string Supporting = "#64748B";  // slate-500    - a live Worker's suppressed red
    public const string Error      = "#B91C1C";  // red-700      - crashed, NOT finished (issue #959)
    public const string Grey       = "#6B7280";  // gray-500     - snoozed, exited, or indeterminate

    /// <summary>
    /// The BROKEN sentinel - magenta. Not a state. It means "the Gateway sent this desktop a colour
    /// name it does not know", and it exists to be impossible to misread.
    ///
    /// This arm used to be <c>_ =&gt; GreyBrush</c>, which was a fallback of the exact class this
    /// mission exists to kill: GREY IS A REAL STATE HERE - it means snoozed or exited - so an
    /// unrecognised colour rendering grey was not a neutral "we do not know", it was an affirmative
    /// lie that the session is parked.
    ///
    /// Why a sentinel and not a throw. A throw was the obvious answer and it is WRONG, by
    /// observation rather than argument: StatusColorBrush is read through a XAML binding, and when a
    /// bound getter throws, Avalonia swallows it and leaves the property unset - the probe rendered
    /// <c>Background = null</c>. That is an INVISIBLE dot: silent AND broken, strictly quieter than
    /// the grey it would have replaced. A log line alone is not loud either - the owner is looking at
    /// the rail, not the log. So: magenta on the dot (loud, and unmistakable for any real state) AND
    /// a logged error (so it is diagnosable), AND - the real fix - a test that drives the REAL fold
    /// across every state it can emit and proves this branch cannot fire. The sentinel is a tripwire
    /// a test guarantees is unreachable, not a guess that fires silently in production.
    /// </summary>
    public const string Broken     = "#FF00FF";  // magenta - NOT a state; "this desktop does not know that colour"

    private static readonly ISolidColorBrush RedBrush        = new SolidColorBrush(Color.Parse(Red));
    private static readonly ISolidColorBrush BlueBrush       = new SolidColorBrush(Color.Parse(Blue));
    private static readonly ISolidColorBrush GreenBrush      = new SolidColorBrush(Color.Parse(Green));
    private static readonly ISolidColorBrush YellowBrush     = new SolidColorBrush(Color.Parse(Yellow));
    private static readonly ISolidColorBrush OrangeBrush     = new SolidColorBrush(Color.Parse(Orange));
    private static readonly ISolidColorBrush PurpleBrush     = new SolidColorBrush(Color.Parse(Purple));
    private static readonly ISolidColorBrush SupportingBrush = new SolidColorBrush(Color.Parse(Supporting));
    private static readonly ISolidColorBrush ErrorBrush      = new SolidColorBrush(Color.Parse(Error));
    private static readonly ISolidColorBrush GreyBrush       = new SolidColorBrush(Color.Parse(Grey));
    private static readonly ISolidColorBrush BrokenBrush     = new SolidColorBrush(Color.Parse(Broken));

    /// <summary>
    /// The brush for a fold colour name. Case-insensitive, because the names cross the wire.
    ///
    /// "unknown" is a REAL fold colour - SessionOrdering emits it for an activity state it does not
    /// recognise - and it maps to grey legitimately: an indeterminate session is not asking for
    /// anything. That is a mapping, not a fallback. Anything OUTSIDE the fold's vocabulary is a bug,
    /// and gets <see cref="Broken"/> plus a logged error rather than a colour that means something
    /// else. See the <see cref="Broken"/> docs for why this is a sentinel and not a throw.
    /// </summary>
    public static ISolidColorBrush BrushFor(string? foldColor) => foldColor?.ToLowerInvariant() switch
    {
        "red"        => RedBrush,
        "blue"       => BlueBrush,
        "green"      => GreenBrush,
        "yellow"     => YellowBrush,
        "orange"     => OrangeBrush,
        "purple"     => PurpleBrush,
        "supporting" => SupportingBrush,
        "error"      => ErrorBrush,
        "grey"       => GreyBrush,
        "unknown"    => GreyBrush,
        _            => BrokenBrush,
    };

    /// <summary>The hex for a fold colour name, for the surfaces that style with strings rather
    /// than brushes. Same table, same rules as <see cref="BrushFor"/>.</summary>
    public static string HexFor(string? foldColor) => foldColor?.ToLowerInvariant() switch
    {
        "red"        => Red,
        "blue"       => Blue,
        "green"      => Green,
        "yellow"     => Yellow,
        "orange"     => Orange,
        "purple"     => Purple,
        "supporting" => Supporting,
        "error"      => Error,
        "grey"       => Grey,
        "unknown"    => Grey,
        _            => Broken,
    };

    /// <summary>
    /// True when <paramref name="foldColor"/> is a name this palette knows. The unreachability test
    /// drives the REAL fold over every state it can emit and asserts this holds for all of them, so
    /// the sentinel arm above is a branch a test guarantees cannot fire rather than a silent guess.
    /// </summary>
    public static bool Knows(string? foldColor) => foldColor?.ToLowerInvariant() is
        "red" or "blue" or "green" or "yellow" or "orange" or "purple" or "supporting" or "error" or "grey" or "unknown";

    /// <summary>
    /// Report a colour name the Gateway emitted and this desktop does not know. Separate from
    /// <see cref="BrushFor"/> (which is called from a binding on every repaint and must stay a pure,
    /// allocation-free read - logging in there would write a line per frame). Callers that fold a
    /// live session call this once when they notice, so the magenta on the dot has a matching line
    /// in the log that says which name it was.
    /// </summary>
    public static void ReportUnknownColor(string? foldColor, string sessionId)
        => FileLog.Write($"[StatusPalette] UNKNOWN FOLD COLOUR '{foldColor}' for session {sessionId} - " +
                         "not in the desktop palette, rendering the BROKEN magenta sentinel. The Gateway is " +
                         "emitting a colour name this build does not know; see docs/new_architecture/session-state.html.");
}
