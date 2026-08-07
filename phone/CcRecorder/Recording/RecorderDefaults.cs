namespace CcRecorder.Recording;

/// <summary>
/// Built-in defaults for the recorder. The saved <c>gateway_url</c> preference
/// always wins over these; a configured device never reads them again.
/// </summary>
public static class RecorderDefaults
{
    /// <summary>
    /// Default CC Director Gateway base URL. This is the HOSTED Gateway - the
    /// one the phone actually records against (recorder-background-capture-decision
    /// mission). A self-hosted setup edits the server field in the app UI.
    /// </summary>
    public const string GatewayUrl = "https://gateway.devthrottle.com";
}
