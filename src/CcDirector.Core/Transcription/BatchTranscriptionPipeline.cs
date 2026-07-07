using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// The ONE shared batch transcription pipeline every surface calls (issue #587, design gap items
/// G1/G4/G5). It is deliberately the only transcription entry point outside the live-dictation UI:
///
///   1. Whole audio in. A complete, already-gated audio blob (the Audio Completeness Gate, issue
///      #586, runs upstream - this pipeline assumes the bytes it receives are complete). There is
///      NO streaming/partial transcription here: the whole clip is transcribed once.
///   2. ONE batch request to the resolved method. The OpenAI-compatible
///      <c>POST {baseUrl}/audio/transcriptions</c> that BOTH OpenAI and the DevThrottle/Groq proxy
///      implement. The base URL, key, and model come entirely from the caller's
///      <see cref="ResolvedTranscription"/> (produced by the Gateway routing resolver,
///      <see cref="TranscriptionKeyResolver.ResolveEndpointAsync"/>), so the user-selected method governs
///      every path and the bring-your-own OpenAI key is never crossed onto the devthrottle.com URL.
///   3. Dictionary corrector ONLY. The raw transcript runs through the validated dictionary
///      corrector (<see cref="CleanupOrchestrator"/> + <see cref="TranscriptEditEngine"/>): the
///      model proposes find/replace edits, deterministic code validates and applies them to the RAW
///      text. There is NO free-text language-model cleanup - the only text change allowed is swapping
///      a known dictionary term, so a transcript with no dictionary hit comes back byte-identical.
///
/// Routing-and-text decisions (e.g. agent vs wingman, wake-phrase handling) are NOT this pipeline's
/// job and must never alter the transcript it returns; callers that still need them run them as a
/// separate non-text step on a copy of the text.
///
/// Stateless and side-effect-free apart from the network call and logging, so it is safe to call
/// per request.
/// </summary>
public sealed class BatchTranscriptionPipeline : IDisposable
{
    /// <summary>
    /// HTTP timeout for the batch transcription POST. Whole-clip uploads can be several seconds for
    /// longer recordings. The timeout
    /// is per request, so a recording split into several parts gets the full budget for each part.
    /// </summary>
    public static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The largest a single transcription request body may be. A recording over this size is split into
    /// several bounded parts, each transcribed with its own request, so no single upload can exceed the
    /// provider's limit. The DevThrottle managed proxy runs on a serverless function that rejects a body
    /// over roughly 4.5 megabytes with FUNCTION_PAYLOAD_TOO_LARGE / HTTP 413; this budget stays under
    /// that with room to spare for the small multipart framing (the model and format form fields).
    /// </summary>
    public const int MaxTranscriptionUploadBytes = 4_000_000;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cleanupModel;

    /// <param name="httpClient">Optional shared HttpClient (tests inject a stub). The pipeline creates
    /// and owns one when null.</param>
    /// <param name="cleanupModel">The chat model the dictionary corrector uses to PROPOSE edits
    /// (deterministic validation still gates them). Defaults to the dictation default.</param>
    public BatchTranscriptionPipeline(HttpClient? httpClient = null, string? cleanupModel = null)
    {
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel) ? CleanupOrchestrator.DefaultModel : cleanupModel;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TranscribeTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <summary>
    /// Transcribe a complete audio blob using the resolved method, then apply the dictionary
    /// corrector only. Returns the raw transcript, the corrected transcript, and the list of
    /// dictionary terms that changed.
    ///
    /// The dictionary correction fails open (issue #190 contract): on an empty dictionary, a missing
    /// key, or any cleanup error the corrected text equals the raw text and the change list is empty,
    /// so a dictionary problem never costs the user their words. Transcription itself does NOT fail
    /// open - if the provider rejects the request it throws, because a missing transcript is a real
    /// failure the caller must surface, not paper over.
    /// </summary>
    /// <param name="audio">The complete audio bytes (already gated upstream).</param>
    /// <param name="fileName">Filename hint for the multipart upload; its extension tells the server how to decode the bytes.</param>
    /// <param name="routing">The resolved method: base URL, key, and model from the Gateway routing resolver.</param>
    /// <param name="dictionary">The dictionary the corrector uses; pass <see cref="DictationDictionary.Empty"/> for none.</param>
    /// <param name="profileName">Dictation profile selecting whether correction runs. Defaults to "default".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BatchTranscriptionResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        ResolvedTranscription routing,
        DictationDictionary dictionary,
        string profileName = "default",
        CancellationToken ct = default,
        IProgress<TranscriptionProgress>? progress = null)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (routing is null) throw new ArgumentNullException(nameof(routing));
        if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));
        if (audio.Length == 0)
            throw new ArgumentException("audio blob is empty; the Audio Completeness Gate must run before transcription", nameof(audio));

        FileLog.Write($"[BatchTranscriptionPipeline] TranscribeAsync: bytes={audio.Length}, model={routing.Model}");

        var raw = await TranscribeBatchAsync(audio, fileName, routing, ct, progress);
        FileLog.Write($"[BatchTranscriptionPipeline] raw transcript len={raw.Length}");

        var corrected = await ApplyDictionaryAsync(raw, routing, dictionary, profileName, ct);

        return new BatchTranscriptionResult(
            RawTranscript: raw,
            CorrectedTranscript: corrected.Text,
            DictionaryApplied: corrected.Applied,
            ChangedWords: corrected.ChangedWords,
            Reason: corrected.Reason);
    }

    /// <summary>
    /// Transcribe one complete audio blob to RAW text using the resolved method, with NO dictionary
    /// correction. This is the transcription half of <see cref="TranscribeAsync"/> on its own, for
    /// callers that batch-transcribe several segments and then run the dictionary corrector ONCE on
    /// the assembled concatenation (the phone recorder, issue #591) - so the assembled transcript is
    /// provably the per-segment raw concatenation plus dictionary edits only, never a per-segment
    /// reword. Same single transport: ONE batch POST to <c>{baseUrl}/audio/transcriptions</c> with the
    /// resolved key and model. Throws on a provider error (a missing transcript is a real failure the
    /// caller must surface, never paper over).
    /// </summary>
    /// <param name="audio">The complete audio segment bytes (already gated upstream).</param>
    /// <param name="fileName">Filename hint for the multipart upload; its extension tells the server how to decode the bytes.</param>
    /// <param name="routing">The resolved method: base URL, key, and model from the Gateway routing resolver.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> TranscribeRawAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct = default,
        IProgress<TranscriptionProgress>? progress = null)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (routing is null) throw new ArgumentNullException(nameof(routing));
        if (audio.Length == 0)
            throw new ArgumentException("audio blob is empty; the Audio Completeness Gate must run before transcription", nameof(audio));

        FileLog.Write($"[BatchTranscriptionPipeline] TranscribeRawAsync: bytes={audio.Length}, model={routing.Model}");
        return await TranscribeBatchAsync(audio, fileName, routing, ct, progress);
    }

    /// <summary>
    /// Transcribe a whole audio blob to raw text, splitting it first when it is over the per-request
    /// size limit so no single upload can exceed the provider's body limit. A clip within the budget is
    /// one part = one POST, exactly as before. A long clip is cut into several bounded WAV parts (see
    /// <see cref="WavSplitter"/>), each transcribed with its own POST, and the raw per-part texts are
    /// joined in order. A caller that asked for the dictionary corrector still runs it ONCE on this
    /// assembled text, so the result is provably the raw concatenation plus dictionary edits only - the
    /// same assemble-then-clean contract the phone recorder uses across its segments.
    ///
    /// Only PCM WAV can be split. An over-budget blob that is NOT a splittable WAV fails loud rather
    /// than posting an oversized body, because a silent 413 is exactly the failure this removes (the
    /// no-fallback rule). Every capture surface sends PCM WAV, so the long-recording paths are covered.
    /// </summary>
    private async Task<string> TranscribeBatchAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct,
        IProgress<TranscriptionProgress>? progress = null)
    {
        if (audio.Length <= MaxTranscriptionUploadBytes)
        {
            progress?.Report(new TranscriptionProgress(0, 1));
            var only = await PostOneAsync(audio, fileName, routing, ct);
            progress?.Report(new TranscriptionProgress(1, 1));
            return only;
        }

        if (!WavSplitter.TrySplit(audio, MaxTranscriptionUploadBytes, out var parts) || parts is null)
            throw new InvalidOperationException(
                $"Audio is {audio.Length:N0} bytes, over the {MaxTranscriptionUploadBytes:N0}-byte per-request "
                + "limit, and is not a PCM WAV that can be split into bounded parts. Capture as PCM WAV so long "
                + "recordings can be chunked.");

        FileLog.Write($"[BatchTranscriptionPipeline] split {audio.Length} bytes into {parts.Count} parts "
                      + $"(budget={MaxTranscriptionUploadBytes}), transcribing each");

        progress?.Report(new TranscriptionProgress(0, parts.Count));
        var texts = new List<string>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            var partName = PartFileName(fileName, i);
            var text = await PostOneAsync(parts[i], partName, routing, ct);
            FileLog.Write($"[BatchTranscriptionPipeline] part {i + 1}/{parts.Count}: bytes={parts[i].Length}, chars={text.Length}");
            if (text.Length > 0) texts.Add(text);
            progress?.Report(new TranscriptionProgress(i + 1, parts.Count));
        }

        return string.Join(" ", texts);
    }

    /// <summary>
    /// Name one part of a split upload, keeping the original extension so the server still decodes the
    /// bytes correctly (e.g. <c>dictation.wav</c> -&gt; <c>dictation.0.wav</c>).
    /// </summary>
    private static string PartFileName(string fileName, int index)
    {
        if (string.IsNullOrEmpty(fileName)) return $"audio.{index}.wav";
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem}.{index}{ext}";
    }

    /// <summary>
    /// ONE whole-audio batch POST to the OpenAI-compatible <c>/audio/transcriptions</c> endpoint of
    /// the resolved base URL, presenting the resolved key and model. This is the single transcription
    /// transport for the shared pipeline - there is no streaming/partial path here. Callers over the
    /// per-request size limit are split into several of these by <see cref="TranscribeBatchAsync"/>.
    /// </summary>
    private async Task<string> PostOneAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct)
    {
        var endpoint = routing.BaseUrl.TrimEnd('/') + "/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", routing.ApiKey);

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        // MediaTypeHeaderValue's ctor only accepts a bare "type/subtype"; a parameter suffix
        // (e.g. "audio/webm;codecs=opus") throws. The server detects the codec from the bytes
        // anyway, so a bare type is correct.
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(GuessAudioContentType(fileName));
        form.Add(audioContent, "file", string.IsNullOrEmpty(fileName) ? "audio.webm" : fileName);
        form.Add(new StringContent(routing.Model), "model");
        form.Add(new StringContent("json"), "response_format");
        request.Content = form;

        using var resp = await _http.SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // Out of credits (issue #885): the hosted service returns HTTP 402 with
        // code=insufficient_credits. This is an expected, distinct condition - surface it as a typed
        // exception so the stack preserves the recording and offers "Add credits" rather than treating
        // it as an opaque provider rejection.
        if ((int)resp.StatusCode == 402)
            throw new InsufficientCreditsException(ParseErrorCode(body), $"Transcription returned 402: {Truncate(body, 400)}");

        // Any other non-success status becomes a typed failure carrying the status code, so the durable
        // dictation retry loop (issue #1130) can tell a transient 5xx/429 (retry) from a permanent 4xx
        // (do not retry). The message format is unchanged, and the type still derives from
        // InvalidOperationException so existing catches keep working.
        if (!resp.IsSuccessStatusCode)
            throw new TranscriptionFailedException((int)resp.StatusCode, $"Transcription returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("text", out var textProp))
            throw new InvalidOperationException("Transcription response missing 'text' field");
        return (textProp.GetString() ?? "").Trim();
    }

    /// <summary>
    /// Apply the validated dictionary corrector - the ONLY post-transcription transform. The model
    /// proposes edits and <see cref="TranscriptEditEngine"/> validates/applies them to the RAW text,
    /// so the only possible change is swapping a known dictionary term. Fails open: returns the raw
    /// text unchanged (and an empty change list) on an empty transcript, an empty dictionary, or any
    /// error.
    /// </summary>
    private async Task<CleanupOutcome> ApplyDictionaryAsync(
        string raw, ResolvedTranscription routing, DictationDictionary dictionary, string profileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new CleanupOutcome(raw, Applied: false, Reason: "empty transcript");

        // The corrector talks to a chat-completions endpoint. Use the SAME base URL the transcription
        // was routed to so the key is never crossed onto the wrong provider's URL, and share this
        // pipeline's HttpClient so both calls go through the same transport. The corrector's own
        // fail-open contract turns any cleanup problem into a verbatim passthrough.
        using var cleanup = new CleanupOrchestrator(
            apiKey: routing.ApiKey, model: _cleanupModel, httpClient: _http, baseUrl: routing.BaseUrl);
        return await cleanup.CleanAsync(raw, dictionary, profileName, ct);
    }

    private static string GuessAudioContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Best-effort read of the machine-readable error code from an OpenAI-compatible 402 body. Delegates
    /// to the shared <see cref="HostedAiErrorMapper.ParseErrorCode"/> so the whole stack parses the 402
    /// code in exactly one place (issue #938) - the transcription path and the epic-wide gate never drift.
    /// </summary>
    private static string ParseErrorCode(string body) => HostedAiErrorMapper.ParseErrorCode(body);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// The result of one shared batch transcription (issue #587): the raw transcript, the corrected
/// transcript (dictionary-only), and exactly which dictionary terms were swapped.
/// <see cref="CorrectedTranscript"/> equals <see cref="RawTranscript"/> byte-for-byte whenever no
/// dictionary term matched (<see cref="ChangedWords"/> empty), proving the pipeline never rewords.
/// </summary>
public sealed record BatchTranscriptionResult(
    string RawTranscript,
    string CorrectedTranscript,
    bool DictionaryApplied,
    IReadOnlyList<TranscriptEdit> ChangedWords,
    string? Reason);

/// <summary>
/// Progress of a batch transcription while it runs: how many bounded parts have finished out of the
/// total the clip was split into. A short clip reports one part; a long recording reports one update
/// per part as each finishes, so a surface can show "transcribing part N of M" instead of a silent
/// wait. <see cref="TotalParts"/> is known from the first report (parts are planned before any POST).
/// </summary>
public sealed record TranscriptionProgress(int CompletedParts, int TotalParts);
