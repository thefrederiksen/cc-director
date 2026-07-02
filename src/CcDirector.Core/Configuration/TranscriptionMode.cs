namespace CcDirector.Core.Configuration;

/// <summary>
/// HOW transcription (speech-to-text) connects for this machine (issue #497, #887). The user chooses
/// a capability, not a raw provider key. There are exactly two capabilities - DevThrottle dogfoods its
/// own hosted service by default (issue #887):
///
///   - <see cref="DevThrottle"/>: DevThrottle's hosted transcription on the signed-in account. This is
///     the DEFAULT (issue #887): every AI capability draws on the account's credits with no provider
///     setup. Transcription is served through DevThrottle at <c>https://devthrottle.com/api/v1</c>; the
///     credential is DevThrottle's own account key, not a provider key.
///   - <see cref="Byo"/> ("bring your own key"): the user's own OpenAI key. Transcription goes
///     directly to <c>https://api.openai.com/v1</c> with their <c>sk-</c> key. The key stays on
///     this machine and is NEVER sent to DevThrottle.
///
/// The two keys are stored separately so switching modes never loses the other key. The old local
/// in-process option was removed in issue #887 (we dogfood our own hosted service); an install that
/// was configured for it falls forward to <see cref="DevThrottle"/>.
/// </summary>
public enum TranscriptionMode
{
    /// <summary>Bring your own OpenAI key; call api.openai.com directly. Opt-in.</summary>
    Byo = 0,

    /// <summary>Use the signed-in DevThrottle account's hosted transcription. The default (issue #887).</summary>
    DevThrottle = 1,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionMode"/>. Pure - unit-tested.</summary>
public static class TranscriptionModeExtensions
{
    /// <summary>The config.json string form: "byo" or "devthrottle".</summary>
    public static string ToConfigString(this TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => "byo",
        TranscriptionMode.DevThrottle => "devthrottle",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Parse a config.json value. Null/empty/whitespace yields the default
    /// (<see cref="TranscriptionMode.DevThrottle"/>, issue #887 - DevThrottle dogfoods its own hosted
    /// service, so it is the default when nothing is configured). The legacy value "local" is a
    /// recognized alias that MIGRATES FORWARD to DevThrottle (issue #887 removed the local option; an
    /// install still configured for it must fall forward, not fail). Any other unrecognized value
    /// THROWS with the allowed set named (no-fallback rule: a typo must not silently pick a mode).
    /// </summary>
    public static TranscriptionMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TranscriptionMode.DevThrottle;
        return value.Trim().ToLowerInvariant() switch
        {
            "byo" => TranscriptionMode.Byo,
            "devthrottle" => TranscriptionMode.DevThrottle,
            // Legacy migration (issue #887): the removed local mode falls forward to the hosted default.
            "local" => TranscriptionMode.DevThrottle,
            _ => throw new ArgumentException(
                $"transcription_mode '{value}' is not valid - it must be \"byo\" or \"devthrottle\".", nameof(value)),
        };
    }

    /// <summary>True when <paramref name="value"/> is a recognized mode (for input validation).</summary>
    public static bool IsValid(string? value)
    {
        try { Parse(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
