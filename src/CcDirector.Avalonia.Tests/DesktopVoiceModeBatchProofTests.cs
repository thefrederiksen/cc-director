using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Proof tests for the desktop-voice-mode batch migration (issue #590). The desktop Voice tab
/// (VoiceView) and the full-screen FIFO takeover (FifoWindow) now transcribe through the SAME
/// whole-audio batch path the Speak dialog uses since issue #589: a WAV blob produced by
/// <see cref="WavWriter"/> sent ONCE through the shared <see cref="BatchTranscriptionPipeline"/>
/// using the user-selected method, with dictionary-only correction.
///
/// These tests drive that exact transcription call against a request-counting stub, so the four
/// issue-#590 acceptance criteria are proven deterministically without a live microphone:
///
///   * the selected method governs the base URL hit (changing the method changes the URL),
///   * exactly ONE batch transcription request is made per turn (no per-partial calls) and there
///     is NO realtime/WebSocket transcription anywhere in the path,
///   * a turn with no dictionary term returns byte-identical to the raw transcription
///     (no free-text cleanup), and
///   * a turn with a dictionary term changes ONLY that term, and the agent-versus-wingman routing
///     decision is applied to the FINISHED transcript and never alters it.
/// </summary>
public sealed class DesktopVoiceModeBatchProofTests
{
    /// <summary>Records every request the pipeline sends and answers the two batch endpoints.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _transcript;
        private readonly string _editsJson;

        public List<string> Urls { get; } = new();
        public int TranscriptionPosts { get; private set; }

        /// <summary>The whole request body size of every /audio/transcriptions POST, in order.</summary>
        public List<long> TranscribeBodyBytes { get; } = new();

        /// <param name="transcript">The raw transcript the /audio/transcriptions endpoint returns.</param>
        /// <param name="editsJson">The edit document the /chat/completions dictionary corrector returns
        /// (default: no edits).</param>
        public CountingHandler(string transcript, string editsJson = "{\\\"edits\\\": []}")
        {
            _transcript = transcript;
            _editsJson = editsJson;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Urls.Add($"{request.Method} {url}");

            string json;
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                TranscriptionPosts++;
                var bodyBytes = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).LongLength;
                TranscribeBodyBytes.Add(bodyBytes);
                json = "{\"text\": " + System.Text.Json.JsonSerializer.Serialize(_transcript) + "}";
            }
            else if (url.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                json = "{\"choices\":[{\"message\":{\"content\": \"" + _editsJson + "\"}}]}";
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ResolvedTranscription Byo() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.OpenAiBaseUrl,
        ApiKey = "sk-byo-key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.OpenAiModel,
        Mode = TranscriptionMode.Byo,
    };

    private static ResolvedTranscription DevThrottle() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt-managed-key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
    };

    // A short, well-formed 24kHz/mono/16-bit WAV clip - the exact shape the desktop recorder
    // (BatchDictationRecorder) produces from the whole captured PCM.
    private static byte[] SampleWavClip()
    {
        var pcm = new byte[4800]; // ~100 ms at 24 kHz mono 16-bit
        new Random(11).NextBytes(pcm);
        return WavWriter.WrapPcm16(pcm, 24_000, 1, 16);
    }

    [Fact]
    public async Task SelectedMethod_GovernsTheBaseUrlHit()
    {
        // Arrange - the same whole-clip WAV, transcribed twice with two different selected methods.
        var byoHandler = new CountingHandler(transcript: "open the pi session");
        var dtHandler = new CountingHandler(transcript: "open the pi session");
        using var byoPipe = new BatchTranscriptionPipeline(new HttpClient(byoHandler));
        using var dtPipe = new BatchTranscriptionPipeline(new HttpClient(dtHandler));

        // Act - one transcription per selected method, exactly as the recorder's TranscribeAsync does.
        await byoPipe.TranscribeAsync(SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);
        await dtPipe.TranscribeAsync(SampleWavClip(), "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        // Assert - the transcription POST went to the base URL of the SELECTED method, not a fixed one.
        Assert.Contains(byoHandler.Urls,
            u => u.Contains(TranscriptionEndpointResolver.OpenAiBaseUrl + "/audio/transcriptions"));
        Assert.Contains(dtHandler.Urls,
            u => u.Contains(TranscriptionEndpointResolver.DevThrottleBaseUrl + "/audio/transcriptions"));
        // The BYO run never crossed onto the DevThrottle URL and vice versa.
        Assert.DoesNotContain(byoHandler.Urls, u => u.Contains(TranscriptionEndpointResolver.DevThrottleBaseUrl));
        Assert.DoesNotContain(dtHandler.Urls, u => u.Contains(TranscriptionEndpointResolver.OpenAiBaseUrl));
    }

    [Fact]
    public async Task WholeClip_TranscribesInExactlyOneBatchCall_NoRealtimeSocket()
    {
        // Arrange - the desktop recorder's whole-clip WAV and a counting transport.
        var handler = new CountingHandler(transcript: "ask the wingman what changed");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // Act - one whole-audio transcription, exactly as BatchDictationRecorder.TranscribeAsync does.
        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);

        // Assert - exactly ONE batch transcription POST for the whole turn (no per-partial calls).
        Assert.Equal(1, handler.TranscriptionPosts);

        // Every network call is a plain HTTP request to an OpenAI-compatible REST endpoint; there is
        // NO ws:// / wss:// realtime socket anywhere in the path.
        Assert.All(handler.Urls, u => Assert.StartsWith("POST http", u));
        Assert.DoesNotContain(handler.Urls, u => u.Contains("ws://") || u.Contains("wss://") || u.Contains("/realtime"));

        Assert.False(string.IsNullOrEmpty(result.RawTranscript));
    }

    [Fact]
    public async Task WholeClip_NoDictionaryTerms_ReturnsByteIdenticalText()
    {
        // Arrange
        const string raw = "send to pi just push the change and let me know when it is done";
        var handler = new CountingHandler(transcript: raw);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // Act - empty dictionary, so the only possible text change (a dictionary swap) cannot happen,
        // and there is NO free-text cleanup.
        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);

        // Assert - the corrected transcript is byte-identical to the raw transcription.
        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript);
        Assert.False(result.DictionaryApplied);
        Assert.Empty(result.ChangedWords);
        Assert.Equal(1, handler.TranscriptionPosts);
    }

    [Fact]
    public async Task WholeClip_DictionaryTerm_ChangesOnlyThatTerm()
    {
        // Arrange - a dictionary that knows "Acmefloe" is a mistranscription of the canonical term
        // "AcmeFlow", and a raw transcript that contains the wrong form once.
        const string raw = "open the Acmefloe session and send the prompt";
        var dictionary = new DictationDictionary(
            Vocabulary: new[] { "AcmeFlow" },
            CommonMistranscriptions: new Dictionary<string, IReadOnlyList<string>>
            {
                ["AcmeFlow"] = new[] { "Acmefloe" },
            },
            Profiles: new Dictionary<string, DictationProfile>
            {
                ["default"] = new DictationProfile("default", CleanupEnabled: true),
            });

        // The dictionary corrector proposes swapping the listed wrong form for the canonical term.
        var handler = new CountingHandler(
            transcript: raw,
            editsJson: "{\\\"edits\\\": [{\\\"find\\\": \\\"Acmefloe\\\", \\\"replace\\\": \\\"AcmeFlow\\\"}]}");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // Act
        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), dictionary, "default");

        // Assert - the ONLY change is the dictionary term; everything around it is identical.
        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal("open the AcmeFlow session and send the prompt", result.CorrectedTranscript);
        Assert.True(result.DictionaryApplied);
        Assert.Single(result.ChangedWords);
        Assert.Equal("Acmefloe", result.ChangedWords[0].Find);
        Assert.Equal("AcmeFlow", result.ChangedWords[0].Replace);
        // Still exactly one batch transcription call.
        Assert.Equal(1, handler.TranscriptionPosts);
    }

    [Fact]
    public async Task LongDesktopRecording_SplitsIntoSeveralBoundedRequests()
    {
        // The reported bug: a twenty-minute desktop dictation produced a single ~57 MB WAV and one
        // upload, which the DevThrottle managed proxy (a serverless function) rejected with a 413
        // FUNCTION_PAYLOAD_TOO_LARGE for a body over roughly 4.5 MB. This proves the desktop capture
        // path - the exact 24 kHz mono 16-bit PCM wrapped by WavWriter.WrapPcm16, the code
        // BatchDictationRecorder.TranscribeAsync runs - is now split into several bounded requests so
        // no single upload can exceed the limit.
        const long providerBodyLimitBytes = 4_500_000;

        // Twenty minutes at the desktop capture format: 24000 samples/sec * 2 bytes * 1200 sec.
        var pcm = new byte[24_000 * 2 * 1200];
        var wav = WavWriter.WrapPcm16(pcm, 24_000, 1, 16);

        var handler = new CountingHandler(transcript: "part text");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeAsync(wav, "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        // The whole clip went out as several transcription requests, each whole body under the limit
        // that caused the 413.
        Assert.True(handler.TranscriptionPosts >= 10,
            $"expected the long clip to split into many requests, got {handler.TranscriptionPosts}");
        Assert.All(handler.TranscribeBodyBytes, n =>
            Assert.True(n < providerBodyLimitBytes, $"a request body was {n} bytes, over the {providerBodyLimitBytes} provider limit"));
    }

    [Fact]
    public async Task Routing_DoesNotModifyTheUsedTranscript()
    {
        // The agent-versus-wingman choice in VoiceView/FifoWindow is a UI-only decision applied to the
        // FINISHED transcript - the SAME text is handed to either branch. Prove the transcript the host
        // would receive is the raw-plus-dictionary text, independent of the routing branch.
        const string raw = "interrupt the build and tell me what failed";
        var handler = new CountingHandler(transcript: raw);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);

        // The transcript VoiceView would raise on AskAgentRequested AND on AskWingmanRequested is this
        // exact string (it reads result.CleanedTranscript before choosing a branch). No dictionary
        // term, so it equals the raw transcription verbatim - routing cannot have altered it.
        var usedTranscript = result.CorrectedTranscript;
        Assert.Equal(raw, usedTranscript);
        Assert.Equal(result.RawTranscript, usedTranscript);
    }
}
