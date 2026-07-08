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
/// endpoint with a SHORT per-attempt timeout and a single retry.
///
/// The upstream speech model (the DevThrottle proxy forwarding to DeepInfra's Kokoro) is bimodal: a
/// healthy call returns in about a second, but an intermittently cold or overloaded worker never
/// answers. The former flat 60-second client timeout with no retry turned one such stall into a full
/// 60-second freeze on the phone (every wingman turn hung for a minute). A 15-second per-attempt cap
/// fails fast on the stalled worker, and one retry almost always lands on a warm worker - so the
/// caller sees about a second normally and about sixteen seconds in the worst realistic case instead
/// of a minute.
///
/// This is NOT a fallback that hides a failure: a genuine provider response (success, 402, any 4xx or
/// 5xx) is returned immediately with no retry, and when BOTH attempts exceed the per-attempt cap the
/// caller is told the synthesis failed (a thrown <see cref="TimeoutException"/>). Only the transient
/// "the worker never answered in time" case is retried.
/// </summary>
internal static class TtsSynthesis
{
    /// <summary>Per-attempt ceiling. Well above the roughly one-second healthy latency, well below the
    /// former 60-second freeze.</summary>
    public static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Total attempts (that is, one retry). The retry targets the transient stalled-worker case.</summary>
    public const int Attempts = 2;

    /// <summary>
    /// POST <c>{ model, voice, input, response_format }</c> to <paramref name="url"/> with bearer
    /// <paramref name="key"/>. Returns the provider's response (success or error) for the caller to
    /// read and dispose. Throws <see cref="TimeoutException"/> when every attempt exceeds
    /// <see cref="PerAttemptTimeout"/>. Honors <paramref name="ct"/>: caller cancellation propagates
    /// immediately and is never mistaken for a per-attempt timeout.
    /// </summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string url, string key, object payload, CancellationToken ct)
    {
        TimeoutException? lastTimeout = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAttempt.CancelAfter(PerAttemptTimeout);
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
                    $"text-to-speech attempt {attempt} exceeded {PerAttemptTimeout.TotalSeconds:0}s");
                FileLog.Write($"[TtsSynthesis] attempt {attempt}/{Attempts} timed out after " +
                    $"{PerAttemptTimeout.TotalSeconds:0}s{(attempt < Attempts ? "; retrying" : "; giving up")}");
            }
        }
        throw lastTimeout!;
    }
}
