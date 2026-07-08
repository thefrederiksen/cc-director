namespace CcDirector.Core.Configuration;

/// <summary>
/// Which Gateway-owned transcription capability this machine uses. DevThrottle is the only supported
/// production capability: every AI feature draws on the signed-in DevThrottle account and the Gateway
/// calls DevThrottle's hosted service using the DevThrottle account key.
///
/// Legacy installs may still have older provider values in config.json; parsing migrates those values
/// forward to <see cref="DevThrottle"/> so upgrades do not break.
/// </summary>
public enum TranscriptionMode
{
    /// <summary>Legacy value retained only so old serialized/test values compile; parsing maps it to DevThrottle.</summary>
    Byo = 0,

    /// <summary>Use the signed-in DevThrottle account's hosted transcription. The only supported mode.</summary>
    DevThrottle = 1,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionMode"/>. Pure - unit-tested.</summary>
public static class TranscriptionModeExtensions
{
    /// <summary>The config.json string form. Legacy values serialize forward as "devthrottle".</summary>
    public static string ToConfigString(this TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => "devthrottle",
        TranscriptionMode.DevThrottle => "devthrottle",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Parse a config.json value. Null/empty/whitespace and every recognized legacy provider value
    /// yields <see cref="TranscriptionMode.DevThrottle"/>. Unknown values still throw so a typo is
    /// visible instead of silently accepted.
    /// </summary>
    public static TranscriptionMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TranscriptionMode.DevThrottle;
        return value.Trim().ToLowerInvariant() switch
        {
            "devthrottle" => TranscriptionMode.DevThrottle,
            // Legacy migrations: removed local/BYO provider choices now fall forward to DevThrottle.
            "byo" => TranscriptionMode.DevThrottle,
            "openai" => TranscriptionMode.DevThrottle,
            "local" => TranscriptionMode.DevThrottle,
            _ => throw new ArgumentException(
                $"transcription_mode '{value}' is not valid - DevThrottle is the only supported mode.", nameof(value)),
        };
    }

    /// <summary>True when <paramref name="value"/> is a recognized mode (for input validation).</summary>
    public static bool IsValid(string? value)
    {
        try { Parse(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
