namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The wingman model call was rejected with HTTP 429 (too many requests): the provider is rate
/// limiting us (issue #1324). Calling it again immediately just earns another 429 - the storm that
/// left every voice session showing "no narration". This carries the provider's Retry-After hint when
/// it sent one, so the caller can back off for exactly as long as asked instead of guessing.
///
/// It extends <see cref="InvalidOperationException"/> so every existing catch of the general
/// wingman-call failure keeps catching a 429 unchanged; only code that wants to treat a rate limit
/// specially (the voice generator's cooldown) catches this exact type.
/// </summary>
public sealed class WingmanModelRateLimitedException : InvalidOperationException
{
    /// <summary>How long the provider asked us to wait before retrying (its Retry-After header), or
    /// null when it gave no hint - the caller then uses its own exponential backoff.</summary>
    public TimeSpan? RetryAfter { get; }

    public WingmanModelRateLimitedException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;
}
