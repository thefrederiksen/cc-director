namespace CcDirector.Core.Transcription;

/// <summary>
/// Thrown by the batch transcription transport when the provider returns a non-success HTTP status
/// (other than 402 out-of-credits, which has its own <see cref="InsufficientCreditsException"/>).
/// Carries the status code so callers can tell a TRANSIENT failure (the provider was briefly slow or
/// unavailable - retry) from a PERMANENT one (the request itself is wrong - do not retry).
///
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch the previous
/// generic exception keep working unchanged; the message format is identical too. What is new is the
/// typed <see cref="StatusCode"/> and the <see cref="IsTransient"/> classification the durable
/// dictation retry loop (issue #1130) relies on.
/// </summary>
public sealed class TranscriptionFailedException : InvalidOperationException
{
    /// <summary>The HTTP status code the transcription provider returned.</summary>
    public int StatusCode { get; }

    public TranscriptionFailedException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// True when the status is one a retry can plausibly clear: request timeout (408), too-early (425),
    /// rate limit (429), or any server-side 5xx (500/502/503/504 - the DevThrottle proxy returns 504
    /// upstream_timeout when the speech provider is slow). A 4xx other than these is the caller's
    /// request being rejected and will fail again identically, so it is NOT retried.
    /// </summary>
    public bool IsTransient => IsTransientStatus(StatusCode);

    /// <summary>Classify a raw HTTP status the same way <see cref="IsTransient"/> does.</summary>
    public static bool IsTransientStatus(int statusCode)
        => statusCode is 408 or 425 or 429 || (statusCode >= 500 && statusCode <= 599);
}
