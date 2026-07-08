using System.Net;
using System.Text;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Transcription;

/// <summary>
/// Tests that the shared pipeline splits an over-limit recording into several bounded transcription
/// requests instead of one oversized POST. This is the regression guard for the 413
/// FUNCTION_PAYLOAD_TOO_LARGE the DevThrottle managed proxy returned for long desktop recordings: the
/// proxy runs on a serverless function that rejects a body over roughly 4.5 megabytes, so a single
/// twenty-minute WAV (tens of megabytes) failed outright. After the fix no single request can exceed
/// <see cref="BatchTranscriptionPipeline.MaxTranscriptionUploadBytes"/>.
/// </summary>
public sealed class BatchTranscriptionPipelineChunkingTests
{
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // The provider ceiling this whole fix protects against: the DevThrottle managed proxy runs on a
    // serverless function that rejects a request body over roughly 4.5 megabytes. The audio-part budget
    // is set below this so the audio plus the small multipart framing (boundaries, the model and format
    // form fields) always fits under it.
    private const long ProviderBodyLimitBytes = 4_500_000;

    // Records every /audio/transcriptions request body size, and answers each with a transcript keyed to
    // the CHUNK INDEX from the part's filename (dictation.{idx}.wav). Because chunks now transcribe in
    // parallel, keying on the filename - not arrival order - lets a test prove the pipeline joins the
    // parts in ORIGINAL order regardless of which request the fake answers first. Thread-safe: parallel
    // chunk requests hit this handler concurrently.
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<long> _bodyBytes = new();

        /// <summary>Request body sizes, in the order they were received (arrival order).</summary>
        public IReadOnlyList<long> TranscribeBodyBytes { get { lock (_lock) return _bodyBytes.ToArray(); } }

        /// <summary>Optional: chunk indices to fail with a 500, to prove one bad chunk fails the job.</summary>
        public HashSet<int> FailChunkIndices { get; } = new();

        /// <summary>Peak number of transcription requests in flight at once (proves the concurrency cap).</summary>
        public int MaxConcurrent;
        private int _inFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                int now = Interlocked.Increment(ref _inFlight);
                lock (_lock) { if (now > MaxConcurrent) MaxConcurrent = now; }
                try
                {
                    int idx = ChunkIndexFromFilename(request);
                    var bytes = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).LongLength;
                    lock (_lock) _bodyBytes.Add(bytes);
                    await Task.Delay(15, ct);  // hold briefly so siblings pile up and concurrency is observable
                    if (FailChunkIndices.Contains(idx))
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        { Content = new StringContent("{\"error\":{\"message\":\"boom\"}}", Encoding.UTF8, "application/json") };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent($"{{\"text\": \"seg{idx}\"}}", Encoding.UTF8, "application/json") };
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
            // The dictionary corrector's chat endpoint: return no edits so the corrected text equals raw.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"edits\\\": []}\"}}]}", Encoding.UTF8, "application/json"),
            };
        }

        // dictation.3.wav -> 3 ; a single-shot dictation.wav (no numeric part) -> 0.
        private static int ChunkIndexFromFilename(HttpRequestMessage request)
        {
            string file = "";
            if (request.Content is MultipartFormDataContent mp)
                foreach (var part in mp)
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "file")
                    { file = part.Headers.ContentDisposition?.FileName?.Trim('"') ?? ""; break; }
            var tokens = file.Split('.');
            return tokens.Length >= 3 && int.TryParse(tokens[^2], out var n) ? n : 0;
        }
    }

    private static ResolvedTranscription DevThrottle() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt_live_key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
    };

    private static byte[] LongWav()
    {
        // Comfortably over three times the budget so it must split into several parts.
        int pcmLength = BatchTranscriptionPipeline.MaxTranscriptionUploadBytes * 3 + 100_000;
        return PcmWav.Wrap(new byte[pcmLength], SampleRate, Channels, BitsPerSample);
    }

    [Fact]
    public async Task TranscribeRawAsync_OverBudgetWav_SplitsIntoSeveralBoundedRequests()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var text = await pipeline.TranscribeRawAsync(LongWav(), "dictation.wav", DevThrottle());

        // More than one request was sent, and each whole request body (audio plus multipart framing)
        // stayed under the provider ceiling that caused the 413.
        Assert.True(handler.TranscribeBodyBytes.Count >= 4, $"expected several parts, got {handler.TranscribeBodyBytes.Count}");
        Assert.All(handler.TranscribeBodyBytes, n =>
            Assert.True(n < ProviderBodyLimitBytes,
                $"a request body was {n} bytes, over the {ProviderBodyLimitBytes} provider limit"));

        // The per-part transcripts are joined in order.
        var expected = string.Join(" ", Enumerable.Range(0, handler.TranscribeBodyBytes.Count).Select(i => $"seg{i}"));
        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task TranscribeAsync_OverBudgetWav_SplitsAndAppliesDictionaryOnceOnAssembledText()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            LongWav(), "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        Assert.True(handler.TranscribeBodyBytes.Count >= 4);
        // With an empty dictionary the corrected text is the raw assembled concatenation, byte-identical.
        var expected = string.Join(" ", Enumerable.Range(0, handler.TranscribeBodyBytes.Count).Select(i => $"seg{i}"));
        Assert.Equal(expected, result.RawTranscript);
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript);
    }

    [Fact]
    public async Task TranscribeRawAsync_OverBudgetNonWav_FailsLoudRatherThanPostingOversizedBody()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // A compressed blob over the budget cannot be split; the pipeline must refuse rather than 413.
        var tooBig = new byte[BatchTranscriptionPipeline.MaxTranscriptionUploadBytes + 1];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.TranscribeRawAsync(tooBig, "recording.webm", DevThrottle()));
        Assert.Empty(handler.TranscribeBodyBytes); // nothing was posted
    }

    [Fact]
    public async Task BrowserDictationWrap_LongRecording_SplitsIntoSeveralBoundedRequests()
    {
        // The browser and Blazor dictation endpoint (DictationEndpoint, the /dictate WebSocket) streams
        // PCM16 to the server, accumulates the whole clip, and wraps it with PcmWav.Wrap at 24 kHz mono
        // 16-bit before calling this pipeline - the exact server-side step. This proves a twenty-minute
        // browser recording wrapped that way is split into several bounded requests instead of one
        // oversized POST that the DevThrottle proxy would reject with a 413.
        var pcm = new byte[24000 * 2 * 1200]; // twenty minutes at the /dictate capture format
        var wav = PcmWav.Wrap(pcm, 24000, 1, 16);

        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeAsync(wav, "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        Assert.True(handler.TranscribeBodyBytes.Count >= 10,
            $"expected the long browser clip to split into many requests, got {handler.TranscribeBodyBytes.Count}");
        Assert.All(handler.TranscribeBodyBytes, n =>
            Assert.True(n < ProviderBodyLimitBytes, $"a request body was {n} bytes, over the {ProviderBodyLimitBytes} provider limit"));
    }

    [Fact]
    public async Task TranscribeRawAsync_WithinBudget_StillOneRequest()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var wav = PcmWav.Wrap(new byte[2000], SampleRate, Channels, BitsPerSample);
        await pipeline.TranscribeRawAsync(wav, "dictation.wav", DevThrottle());

        Assert.Single(handler.TranscribeBodyBytes);
    }

    [Fact]
    public async Task TranscribeRawAsync_OverBudgetWav_ReportsPerPartProgress()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var reports = new List<TranscriptionProgress>();
        var progress = new SyncProgress<TranscriptionProgress>(reports.Add);

        await pipeline.TranscribeRawAsync(LongWav(), "dictation.wav", DevThrottle(), default, progress);

        var partCount = handler.TranscribeBodyBytes.Count;
        Assert.True(partCount >= 4);
        // The first report announces the total with zero done (fired before any chunk runs). Chunks now
        // complete in PARALLEL, so completion reports arrive in a non-deterministic order - but there is
        // still exactly one per chunk plus the announcement, every TotalParts is the count, and the
        // completed values are exactly 0..partCount with none missing or repeated.
        Assert.Equal(new TranscriptionProgress(0, partCount), reports[0]);
        Assert.Equal(partCount + 1, reports.Count);
        Assert.All(reports, r => Assert.Equal(partCount, r.TotalParts));
        Assert.Equal(
            Enumerable.Range(0, partCount + 1),
            reports.Select(r => r.CompletedParts).OrderBy(x => x));
    }

    [Fact]
    public async Task TranscribeRawAsync_WithinBudget_ReportsSinglePart()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var reports = new List<TranscriptionProgress>();
        var wav = PcmWav.Wrap(new byte[2000], SampleRate, Channels, BitsPerSample);
        await pipeline.TranscribeRawAsync(wav, "dictation.wav", DevThrottle(), default, new SyncProgress<TranscriptionProgress>(reports.Add));

        // A short clip is one part: total is 1 throughout, so a UI shows no per-part counter.
        Assert.All(reports, r => Assert.Equal(1, r.TotalParts));
        Assert.Equal(new TranscriptionProgress(1, 1), reports[^1]);
    }

    [Fact]
    public async Task TranscribeRawAsync_LongWav_TranscribesChunksInParallel_BoundedByTheCap()
    {
        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeRawAsync(LongWav(), "dictation.wav", DevThrottle());

        Assert.True(handler.TranscribeBodyBytes.Count >= 4);
        // Actually ran several at once (proves parallelism)...
        Assert.True(handler.MaxConcurrent >= 2, $"expected concurrent chunks, peak was {handler.MaxConcurrent}");
        // ...but never more than the cap (proves the bound that protects rate limits / the breaker).
        Assert.True(handler.MaxConcurrent <= BatchTranscriptionPipeline.MaxParallelChunks,
            $"peak concurrency {handler.MaxConcurrent} exceeded the cap {BatchTranscriptionPipeline.MaxParallelChunks}");
    }

    [Fact]
    public async Task TranscribeRawAsync_OneChunkFails_JobFailsCleanly_NoSilentPartial()
    {
        var handler = new CountingHandler();
        handler.FailChunkIndices.Add(2);   // the third chunk returns HTTP 500 on every attempt
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // The whole job surfaces the failure (the proxy's typed 5xx) - it never returns a partial join.
        await Assert.ThrowsAsync<TranscriptionFailedException>(() =>
            pipeline.TranscribeRawAsync(LongWav(), "dictation.wav", DevThrottle()));
    }

    [Fact]
    public async Task TranscribeRawAsync_UnderByteBudgetButLong_StillSplitsByDuration()
    {
        // 120 s of 16 kHz mono 16-bit = 3.84 MB: UNDER the 4 MB byte budget but well over the 90 s max
        // chunk duration. The old bytes-only gate sent this as ONE slow request; duration splitting now
        // breaks it into several fast ones.
        var wav = PcmWav.Wrap(new byte[120 * 16000 * 2], 16000, 1, 16);
        Assert.True(wav.Length < BatchTranscriptionPipeline.MaxTranscriptionUploadBytes);

        var handler = new CountingHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));
        await pipeline.TranscribeRawAsync(wav, "dictation.wav", DevThrottle());

        Assert.True(handler.TranscribeBodyBytes.Count >= 2,
            $"expected a long-but-small clip to split by duration, got {handler.TranscribeBodyBytes.Count} request(s)");
    }

    // A thread-safe IProgress: chunks now complete in parallel, so reports arrive from worker threads.
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;
        private readonly object _lock = new();
        public SyncProgress(Action<T> on) => _on = on;
        public void Report(T value) { lock (_lock) _on(value); }
    }
}
