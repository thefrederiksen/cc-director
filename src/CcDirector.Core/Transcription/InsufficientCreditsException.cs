namespace CcDirector.Core.Transcription;

/// <summary>
/// The DevThrottle hosted service reported the account is out of credits (HTTP 402,
/// <c>code=insufficient_credits</c>) for a transcription request (issue #885). This is a distinct,
/// expected condition - NOT a provider rejection - so the whole stack can handle it uniformly: the
/// action stops cleanly, the user's recording is preserved and retryable, and the UI offers "Add
/// credits" and "Use my own OpenAI key" instead of a raw error. Thrown by
/// <see cref="BatchTranscriptionPipeline"/> when the hosted endpoint returns 402; the Gateway
/// transcription owner turns it into an out-of-credits result the HTTP face maps to 402.
/// </summary>
public sealed class InsufficientCreditsException : Exception
{
    /// <summary>The machine-readable code the hosted service returned (e.g. "insufficient_credits").</summary>
    public string Code { get; }

    public InsufficientCreditsException(string code, string message) : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "insufficient_credits" : code;
    }
}
