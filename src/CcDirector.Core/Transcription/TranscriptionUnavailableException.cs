namespace CcDirector.Core.Transcription;

/// <summary>
/// Thrown when transcription cannot run because no method is configured: no transcription key is set,
/// or a configured Gateway is unreachable so its routing cannot be fetched. This is NOT a provider
/// rejection of the audio - the audio was never sent. The durable dictation retry loop (issue #1130)
/// treats it as "needs the user to act" (set a key / bring the Gateway up) rather than auto-retrying a
/// call that will keep failing, and keeps the recorded audio saved so it delivers once a method exists.
///
/// Carries the mode-appropriate <see cref="Message"/> from
/// <see cref="Configuration.TranscriptionKeyResolver.UnavailableMessage"/> so the user is told exactly where
/// to set the key.
/// </summary>
public sealed class TranscriptionUnavailableException : Exception
{
    public TranscriptionUnavailableException(string message) : base(message)
    {
    }
}
