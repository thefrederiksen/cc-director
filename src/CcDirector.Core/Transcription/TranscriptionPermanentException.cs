namespace CcDirector.Core.Transcription;

/// <summary>
/// Thrown when a clip CANNOT be transcribed no matter how many times it is retried - a permanent,
/// client-side-shaped failure rather than a transient provider one. The two cases (issue #1139): the
/// audio format cannot be decoded/transcoded to a splittable form, or it is too large to send and
/// cannot be reduced. Retrying is pointless, so the durable dictation loop must STOP (loop-stop), not
/// keep resending. Contrast <see cref="TranscriptionFailedException"/> (a provider HTTP status that may
/// be transient) and <see cref="InsufficientCreditsException"/> (out of credits).
///
/// Derives from <see cref="InvalidOperationException"/> so existing generic catches keep working; what
/// is new is the machine-readable <see cref="Code"/> and the always-false transient classification.
/// </summary>
public sealed class TranscriptionPermanentException : InvalidOperationException
{
    /// <summary>The audio format is not one we can decode/transcode (e.g. an unknown container).</summary>
    public const string UnsupportedFormat = "unsupported_format";

    /// <summary>The audio is too large to send and cannot be transcoded/split down to the limit.</summary>
    public const string AudioTooLarge = "audio_too_large";

    /// <summary>The audio could not be decoded (corrupt, truncated, or empty after decode).</summary>
    public const string NonDecodable = "non_decodable";

    /// <summary>Machine-readable reason (one of the constants above) for the client to map to a stop.</summary>
    public string Code { get; }

    public TranscriptionPermanentException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Always false: this failure is permanent and must never be retried.</summary>
    public bool IsTransient => false;
}
