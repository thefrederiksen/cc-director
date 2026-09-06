namespace CcDirector.Core.Sessions;

/// <summary>How a unit of user input was produced (the DevThrottle Stats modality axis).</summary>
public enum InputModality
{
    /// <summary>Typed on a keyboard.</summary>
    Typed,

    /// <summary>Spoken and transcribed (desktop dictation / voice mode, or phone durable dictation).</summary>
    Voice,
}

/// <summary>Which surface the operator drove from (the DevThrottle Stats surface axis).</summary>
public enum InputSurface
{
    /// <summary>Surface could not be determined. Recorded honestly, but never presented as a real surface
    /// share - the dashboard shows it as its own "unknown" slice rather than folding it into a lie.</summary>
    Unknown,

    /// <summary>The cc-director desktop app on the local machine (input never leaves the machine).</summary>
    Desktop,

    /// <summary>The cockpit web app in a browser.</summary>
    Cockpit,

    /// <summary>The mobile /m app on a phone.</summary>
    Phone,
}

/// <summary>
/// The origin of one unit of user input: its <see cref="InputModality"/> (typed vs voice) and its
/// <see cref="InputSurface"/> (desktop / cockpit / phone). Threaded from each entry point into the two
/// Session choke points (<see cref="Session.SendInput(byte[], InputOrigin?)"/> and
/// <see cref="Session.SendTextAsync(string, SendSource, InputOrigin?)"/>) so the Director can tally, at
/// the ONE place that sees desktop-local input too, how the operator is actually driving development.
///
/// A <c>null</c> origin at a choke point means the caller is framework-internal (handover text, queue
/// drain, framing) and is deliberately NOT counted - it carries no human keystrokes and no spoken words.
/// </summary>
public readonly record struct InputOrigin(InputModality Modality, InputSurface Surface)
{
    /// <summary>Typed at the desktop app. NOT the origin of everything the desktop composer sends: a
    /// transcript inserted into the compose box is spoken, and the box's provenance decides
    /// (<see cref="SpokenTurnRule.ComposerProvenance"/>, ruling R20).</summary>
    public static InputOrigin DesktopTyped => new(InputModality.Typed, InputSurface.Desktop);

    /// <summary>Spoken at the desktop app (desktop dictation / voice mode).</summary>
    public static InputOrigin DesktopVoice => new(InputModality.Voice, InputSurface.Desktop);

    /// <summary>Typed input from the given surface.</summary>
    public static InputOrigin Typed(InputSurface surface) => new(InputModality.Typed, surface);

    /// <summary>Spoken input from the given surface.</summary>
    public static InputOrigin Voice(InputSurface surface) => new(InputModality.Voice, surface);

    /// <summary>
    /// Map a device registry <c>DeviceType</c> (resolved from a verified per-device key) to the REMOTE
    /// surface it represents. Remote input reaching a Director through the Gateway is a phone or a
    /// cockpit browser; anything else is honestly <see cref="InputSurface.Unknown"/> rather than guessed.
    /// Desktop is never resolved this way - local desktop input is <see cref="InputSurface.Desktop"/> by
    /// construction and carries no device header.
    /// </summary>
    public static InputSurface RemoteSurfaceFromDeviceType(string? deviceType) =>
        (deviceType ?? "").Trim().ToLowerInvariant() switch
        {
            "phone" => InputSurface.Phone,
            "browser" => InputSurface.Cockpit,
            _ => InputSurface.Unknown,
        };

    /// <summary>The stable lowercase wire token for this modality ("typed" / "voice").</summary>
    public string ModalityToken => Modality == InputModality.Voice ? "voice" : "typed";

    /// <summary>The stable lowercase wire token for this surface ("desktop" / "cockpit" / "phone" / "unknown").</summary>
    public string SurfaceToken => Surface switch
    {
        InputSurface.Desktop => "desktop",
        InputSurface.Cockpit => "cockpit",
        InputSurface.Phone => "phone",
        _ => "unknown",
    };
}
