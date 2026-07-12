namespace CcDirector.Core.GatewayConnection;

/// <summary>
/// The four visual states the single bottom-left status box paints (design spec section 6). The box
/// replaces the two former boxes (GatewayIndicator + AccountIndicator) with ONE box carrying two check
/// lines. Colors live with the Avalonia surface; this enumeration is the surface-free instruction for
/// which of the four looks to paint.
/// </summary>
public enum GatewayStatusBoxVisual
{
    /// <summary>Needs attention: NotConfigured or ConnectedNotSignedIn. One or both lines show the next action.</summary>
    Amber,

    /// <summary>Working: a handshake is verifying. "Connecting...".</summary>
    Yellow,

    /// <summary>All good: handshake proven AND the Gateway reports signed in. Both lines filled.</summary>
    Green,

    /// <summary>Was working and is now broken (or a first-time connect failed): the failing leg is named.</summary>
    Red,
}

/// <summary>
/// One of the two check lines the status box shows (spec section 6). The <see cref="Marker"/> is the paint
/// state of the line (filled / hollow / working / failed / muted) which the surface maps to a glyph and
/// color; the <see cref="Text"/> is the plain-English label - either the proven fact or the next action.
/// </summary>
/// <param name="Marker">The paint state of this line's marker.</param>
/// <param name="Text">The plain-English label for this line.</param>
public sealed record GatewayStatusLine(GatewayCheckState Marker, string Text);

/// <summary>
/// The complete content the single status box renders (spec section 6): the overall visual state, the two
/// check lines (Gateway reachable, Account signed in), and a tooltip. Colors and glyphs are applied by the
/// surface from the visual and the per-line markers; this record carries no color.
/// </summary>
/// <param name="Visual">Which of the four looks to paint.</param>
/// <param name="Connected">The "Connected" (Gateway reachable) check line.</param>
/// <param name="SignedIn">The "Signed in" (account) check line.</param>
/// <param name="Tooltip">The hover text summarizing the current state and the click action.</param>
public sealed record GatewayStatusBoxContent(
    GatewayStatusBoxVisual Visual,
    GatewayStatusLine Connected,
    GatewayStatusLine SignedIn,
    string Tooltip);

/// <summary>
/// Turns a resolved Gateway connection snapshot into the single status box's content (design spec section
/// 6). This is the pure, unit-tested presenter for the box - it runs the same
/// <see cref="GatewayConnectionStateResolver"/> the panel uses (so the box and the panel can never
/// disagree) and maps the six resolver states down to the box's four visual states, plus the plain-English
/// text of the two check lines. It has NO UI and NO I/O (CodingStyle Section 8: Core has no UI
/// dependencies), following the same pattern as <see cref="Account.AccountIndicatorPresenter"/>.
///
/// The load-bearing rules it carries (spec sections 4, 6):
///   - The four visual states collapse the six resolver states: NotConfigured and ConnectedNotSignedIn are
///     both amber (needs attention); Connecting is yellow; AllGreen is green; ConnectFailed and
///     WasConnectedNowUnreachable are both red.
///   - Each line shows the proven fact when passed, and the next action when pending - so a brand-new box
///     reads "Connect to Gateway" / "Sign in", and a finished box reads "Connected" / "Signed in: email".
///   - A failed handshake names the failing leg on the Connected line (decision 11, no fallback).
///   - A muted/unknown account line is "cannot tell yet", never a false sign-out (decision 3).
/// </summary>
public static class GatewayStatusBoxPresenter
{
    /// <summary>
    /// Describe how the single status box should paint for the given live snapshot.
    /// </summary>
    /// <param name="inputs">The full verification snapshot (the same inputs the panel resolves).</param>
    /// <param name="gatewayHost">The Gateway host name, shown in the tooltip when connected (may be null).</param>
    /// <param name="accountEmail">The signed-in account email, shown on the signed-in line when green (may be null).</param>
    public static GatewayStatusBoxContent Describe(
        GatewayConnectionInputs inputs, string? gatewayHost, string? accountEmail)
    {
        var resolved = GatewayConnectionStateResolver.Resolve(inputs);
        var visual = VisualFor(resolved.State);
        var connected = ConnectedLine(resolved.ConnectedCheck, inputs.FailedLeg);
        var signedIn = SignedInLine(resolved.SignedInCheck, accountEmail);
        var tooltip = Tooltip(resolved.State, gatewayHost, accountEmail);
        return new GatewayStatusBoxContent(visual, connected, signedIn, tooltip);
    }

    /// <summary>Collapse the six resolver states to the box's four visual states (spec section 6 table).</summary>
    public static GatewayStatusBoxVisual VisualFor(GatewayConnectionState state) => state switch
    {
        GatewayConnectionState.Connecting => GatewayStatusBoxVisual.Yellow,
        GatewayConnectionState.AllGreen => GatewayStatusBoxVisual.Green,
        GatewayConnectionState.ConnectFailed => GatewayStatusBoxVisual.Red,
        GatewayConnectionState.WasConnectedNowUnreachable => GatewayStatusBoxVisual.Red,
        // NotConfigured and ConnectedNotSignedIn are both the amber needs-attention look.
        _ => GatewayStatusBoxVisual.Amber,
    };

    // The Connected line reads the proven fact when passed, the next action when pending, "Connecting..."
    // while verifying, and the named leg when it failed (decision 11).
    private static GatewayStatusLine ConnectedLine(GatewayCheckState check, GatewayConnectionFailedLeg leg) => check switch
    {
        GatewayCheckState.Passed => new GatewayStatusLine(check, "Connected"),
        GatewayCheckState.Working => new GatewayStatusLine(check, "Connecting..."),
        GatewayCheckState.Failed => new GatewayStatusLine(check, FailedLegText(leg)),
        // Pending and Unknown both show the next action; there is nothing to report yet.
        _ => new GatewayStatusLine(check, "Connect to Gateway"),
    };

    private static string FailedLegText(GatewayConnectionFailedLeg leg) => leg switch
    {
        GatewayConnectionFailedLeg.Callback => "Gateway cannot reach this Director back",
        GatewayConnectionFailedLeg.OutboundReach => "Cannot reach the Gateway",
        _ => "Connection failed",
    };

    // The Signed-in line shows the identity when green, the next action when pending, and a muted
    // "Checking account..." while the read is in flight or cannot be told yet (never a false sign-out).
    private static GatewayStatusLine SignedInLine(GatewayCheckState check, string? accountEmail) => check switch
    {
        GatewayCheckState.Passed => new GatewayStatusLine(check, SignedInText(accountEmail)),
        GatewayCheckState.Working => new GatewayStatusLine(check, "Checking account..."),
        // Unknown = cannot tell yet (an unreachable/unread account); muted, still points at signing in.
        GatewayCheckState.Unknown => new GatewayStatusLine(check, "Sign in"),
        // Pending (reachable and signed out) is the actionable nudge.
        _ => new GatewayStatusLine(check, "Sign in"),
    };

    private static string SignedInText(string? accountEmail) =>
        string.IsNullOrWhiteSpace(accountEmail) ? "Signed in" : $"Signed in: {accountEmail}";

    private static string Tooltip(GatewayConnectionState state, string? gatewayHost, string? accountEmail)
    {
        var host = string.IsNullOrWhiteSpace(gatewayHost) ? "the Gateway" : $"Gateway on {gatewayHost}";
        return state switch
        {
            GatewayConnectionState.NotConfigured =>
                "No Gateway is connected. Click to find and connect to your Gateway.",
            GatewayConnectionState.Connecting =>
                $"Connecting to {host}. Click to see progress.",
            GatewayConnectionState.ConnectFailed =>
                $"Could not connect to {host}. Click to see the failing leg and the fix.",
            GatewayConnectionState.ConnectedNotSignedIn =>
                $"Connected to {host}, but this device is not signed in. Click to sign in with DevThrottle.",
            GatewayConnectionState.AllGreen =>
                string.IsNullOrWhiteSpace(accountEmail)
                    ? $"Connected to {host} and signed in. Click for details."
                    : $"Connected to {host} and signed in as {accountEmail}. Click for details.",
            GatewayConnectionState.WasConnectedNowUnreachable =>
                $"Was connected to {host}, now unreachable. Click to reconnect.",
            _ => "Click to open the Gateway connection panel.",
        };
    }
}
