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

    // Records every /audio/transcriptions request and its body size, and answers each with a distinct
    // numbered transcript so the assembled join order can be verified.
    private sealed class CountingHandler : HttpMessageHandler
    {
        public List<long> TranscribeBodyBytes { get; } = new();
        private int _n;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                var bytes = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).LongLength;
                TranscribeBodyBytes.Add(bytes);
                var word = $"seg{_n++}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"text\": \"{word}\"}}", Encoding.UTF8, "application/json"),
                };
            }
            // The dictionary corrector's chat endpoint: return no edits so the corrected text equals raw.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"edits\\\": []}\"}}]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ResolvedTranscription DevThrottle() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt_live_key",
        Model = TranscriptionEndpointResolver.DevThrottleModel,
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
        // First report announces the total with zero done; the last report is all parts done.
        Assert.Equal(new TranscriptionProgress(0, partCount), reports[0]);
        Assert.Equal(new TranscriptionProgress(partCount, partCount), reports[^1]);
        // One report per completed part plus the initial announcement, strictly increasing completion.
        Assert.Equal(partCount + 1, reports.Count);
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.Equal(i, reports[i].CompletedParts);
            Assert.Equal(partCount, reports[i].TotalParts);
        }
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

    // A synchronous IProgress so the reports are captured in order on the calling thread.
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;
        public SyncProgress(Action<T> on) => _on = on;
        public void Report(T value) => _on(value);
    }
}
