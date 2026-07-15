using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// Forwards a text-to-speech request to the configured provider-compatible <c>/audio/speech</c>
/// endpoint with a per-attempt deadline DERIVED FROM THE TEXT, and a single retry.
///
/// The upstream speech model (the DevThrottle proxy forwarding to DeepInfra's Kokoro) is bimodal: a
/// healthy call returns in about a second, but an intermittently cold or overloaded worker never
/// answers. A flat 60-second client timeout with no retry turned one such stall into a full
/// 60-second freeze on the phone (every wingman turn hung for a minute). A per-attempt cap fails
/// fast on the stalled worker, and one retry almost always lands on a warm worker.
///
/// A deadline must exist: the provider does not queue (200 concurrent per model, 429 immediately
/// when busy), so a long silence is a genuine hang, not work in progress.
///
/// WHY THE DEADLINE IS COMPUTED AND NOT A CONSTANT (issue #1612). It used to be a flat 15 seconds.
/// That number was only ever right for a 4000-character world, because synthesis scales LINEARLY
/// with the text - about 1.7 ms per character, measured. The 4000 cap and the 15 s deadline were one
/// decision pretending to be two, so raising the cap alone would have converted silent truncation
/// into silent FAILURE: 12,000 characters needs ~21 s and would have blown a 15 s deadline every
/// time. A constant here is the same disease that produced the 4000 in the first place - correct
/// once, never re-derived when the world moved. Derive it from the work and it cannot rot when the
/// cap moves again.
///
/// This is the ONLY deadline on the speech path. The server-side proxy had its own, nested wrongly
/// inside this one, and it was deleted (devthrottle_internal#360) - a proxy must not invent policy.
/// Do NOT reintroduce a second one anywhere: that nesting was the original bug.
///
/// This is NOT a fallback that hides a failure: a genuine provider response (success, 402, any 4xx or
/// 5xx) is returned immediately with no retry, and when BOTH attempts exceed the deadline the
/// caller is told the synthesis failed (a thrown <see cref="TimeoutException"/>). Only the transient
/// "the worker never answered in time" case is retried.
/// </summary>
internal static class TtsSynthesis
{
    /// <summary>Fixed cost allowed for everything that is not synthesis: TLS, auth, the credit
    /// pre-flight, and the network both ways. It does not scale with the text.</summary>
    private static readonly TimeSpan DeadlineBase = TimeSpan.FromSeconds(5);

    /// <summary>Allowance per character of input. Synthesis measures ~1.7 ms/char, so 4 ms/char is
    /// roughly 2.4x headroom at every length - enough for a slow-but-working worker, short enough
    /// that a genuine hang is still caught quickly.</summary>
    private const double DeadlineMsPerChar = 4.0;

    /// <summary>
    /// The per-attempt deadline for synthesising <paramref name="inputChars"/> characters:
    /// <see cref="DeadlineBase"/> + <see cref="DeadlineMsPerChar"/> per character.
    ///
    /// Checked against measurements taken direct to the provider (2026-07-15, production key), which
    /// is where the 2.4-2.9x headroom claim comes from - not from arithmetic on faith:
    ///   4,000 chars -> synthesis 7.3 s, deadline 21 s (2.9x)
    ///   5,000 chars -> synthesis 11.6 s, deadline 25 s (2.2x)
    ///   8,000 chars -> synthesis 14.0 s, deadline 37 s (2.6x)
    ///   12,000 chars -> synthesis 21.1 s, deadline 53 s (2.5x)
    /// A typical narration after the wingman was told to actually summarise (~550 chars, about 30
    /// seconds spoken) gets ~7 s for a call that takes about one.
    /// </summary>
    public static TimeSpan DeadlineFor(int inputChars)
        => DeadlineBase + TimeSpan.FromMilliseconds(DeadlineMsPerChar * Math.Max(0, inputChars));

    /// <summary>Total attempts (that is, one retry). The retry targets the transient stalled-worker case.</summary>
    public const int Attempts = 2;

    /// <summary>
    /// POST <c>{ model, voice, input, response_format }</c> to <paramref name="url"/> with bearer
    /// <paramref name="key"/>. Returns the provider's response (success or error) for the caller to
    /// read and dispose. Throws <see cref="TimeoutException"/> when every attempt exceeds the
    /// deadline for <paramref name="inputChars"/> (see <see cref="DeadlineFor"/>). Honors
    /// <paramref name="ct"/>: caller cancellation propagates immediately and is never mistaken for a
    /// per-attempt timeout.
    /// </summary>
    /// <param name="inputChars">Length of the text being synthesised. The deadline is derived from
    /// it, so pass the length of the text actually in <paramref name="payload"/>.</param>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string url, string key, object payload, int inputChars, CancellationToken ct)
    {
        var deadline = DeadlineFor(inputChars);
        TimeoutException? lastTimeout = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAttempt.CancelAfter(deadline);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            try
            {
                return await http.SendAsync(req, perAttempt.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our per-attempt timer fired (the caller did not cancel): the upstream worker stalled.
                lastTimeout = new TimeoutException(
                    $"text-to-speech attempt {attempt} exceeded {deadline.TotalSeconds:0}s for {inputChars} characters");
                FileLog.Write($"[TtsSynthesis] attempt {attempt}/{Attempts} timed out after " +
                    $"{deadline.TotalSeconds:0}s ({inputChars} chars)" +
                    $"{(attempt < Attempts ? "; retrying" : "; giving up")}");
            }
        }
        throw lastTimeout!;
    }
}
