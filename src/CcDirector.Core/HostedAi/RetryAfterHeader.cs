using System.Net.Http.Headers;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// Read a provider's <c>Retry-After</c> into a positive wait (issue #1324).
///
/// This lives in Core, next to <see cref="HostedAiErrorMapper"/>, because BOTH hosted legs need it and
/// it must behave identically on each: the model leg (chat/translation) and the speech leg. It was
/// private inside HostedInferenceBrain, which is why the speech leg simply dropped the header on the
/// floor and retried on its own schedule - the provider told us how long to wait and we did not listen.
/// One copy, so the two legs cannot drift.
///
/// When the provider gives no usable hint the caller falls back to its own backoff ramp, so a null here
/// is "no hint", never "retry immediately".
/// </summary>
public static class RetryAfterHeader
{
    /// <summary>
    /// Accepts both header forms: a delta in seconds and an absolute HTTP date. A past date or a
    /// non-positive delta is treated as "no hint".
    /// </summary>
    public static TimeSpan? Parse(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (header.Date is { } when)
        {
            var wait = when - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }

    /// <summary>Convenience for a response's headers: reads <c>Retry-After</c> when present.</summary>
    public static TimeSpan? Parse(HttpResponseHeaders? headers) => Parse(headers?.RetryAfter);
}
