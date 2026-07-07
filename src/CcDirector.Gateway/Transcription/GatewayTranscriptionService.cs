using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The single Gateway owner of speech-to-text (issue #839). Every module that needs audio turned
/// into text goes through this one service: it resolves the DevThrottle routing target and the key,
/// runs the OpenAI-compatible batch endpoint on the DevThrottle proxy, and optionally applies the
/// validated dictionary correction. No caller reads the key or talks to the provider on its own.
///
/// This collapses the resolvers that previously each did "figure out the routing, then read the key" -
/// the phone Notes worker, the Settings "Test it" endpoint, and the Gateway wingman-voice batch paths -
/// into ONE place (<see cref="Resolve"/>). The HTTP face of this service is <c>POST /transcription</c>
/// (<see cref="Api.TranscriptionBatchEndpoint"/>).
///
/// The key lives in exactly one store: the Gateway vault file (<see cref="KeyVault"/>). There is no
/// second config.json copy. The (URL, key, model) tuple is composed from the one pure
/// <see cref="TranscriptionEndpointResolver"/>.
///
/// Transcription integrity (CodingStyle section 16): the only post-transcription transform is the
/// validated dictionary corrector (<see cref="CleanupOrchestrator"/> / <see cref="TranscriptEditEngine"/>)
/// reached through <see cref="BatchTranscriptionPipeline"/>; there is no free-text rewrite, and the
/// corrector fails open to the raw transcript in local mode, with no key, or on any error.
/// </summary>
public sealed class GatewayTranscriptionService
{
    private readonly KeyVault _vault;
    private readonly Func<DictationDictionary> _dictionaryProvider;
    private readonly HttpClient? _http;
    private readonly string _cleanupModel;

    /// <param name="vault">The Gateway key vault - the single store for the transcription key.</param>
    /// <param name="dictionaryProvider">Supplies the live dictation dictionary the corrector uses;
    /// invoked fresh per cleanup so a glossary edit takes effect on the next transcription. Defaults
    /// to loading the shared dictionary file from disk.</param>
    /// <param name="http">Optional shared HttpClient for the remote batch transcription and the
    /// dictionary-corrector POST (tests inject a stub). The pipeline creates and owns one when null.</param>
    /// <param name="cleanupModel">The chat model the dictionary corrector uses to PROPOSE edits
    /// (deterministic validation still gates them). Defaults to the dictation default when blank.</param>
    public GatewayTranscriptionService(
        KeyVault vault,
        Func<DictationDictionary>? dictionaryProvider = null,
        HttpClient? http = null,
        string? cleanupModel = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _dictionaryProvider = dictionaryProvider ?? (() => DictionaryLoader.LoadFromDisk(DictionaryPath()));
        _http = http;
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel) ? CleanupOrchestrator.DefaultModel : cleanupModel;
    }

    /// <summary>
    /// THE one place that turns the DevThrottle routing target into a routing target plus the key. The
    /// routing carries the vault key when present, or null when no key is set (the caller then reports
    /// it unavailable - never a baked-in URL, no-fallback rule).
    /// </summary>
    public GatewayTranscriptionRouting Resolve()
    {
        var endpoint = TranscriptionEndpointResolver.Resolve();
        var key = _vault.Get(endpoint.RequireKeyName());
        var hasKey = !string.IsNullOrWhiteSpace(key);
        FileLog.Write($"[GatewayTranscriptionService] Resolve: model={endpoint.Model}, hasKey={hasKey}");
        return new GatewayTranscriptionRouting { Endpoint = endpoint, Key = hasKey ? key : null };
    }

    /// <summary>
    /// Turn a complete audio clip into text using the configured mode, optionally applying the
    /// validated dictionary correction. Returns a result describing the outcome - the expected
    /// failures (no audio, no key for the mode, a provider rejection) are carried as values rather
    /// than thrown, so the HTTP face can map each to a status code in one place. This is the single
    /// audio-to-text entry point the <c>POST /transcription</c> endpoint, the Settings "Test it"
    /// button, and the Gateway wingman-voice batch paths all use.
    /// </summary>
    /// <param name="audio">The complete audio bytes (already gated upstream where applicable).</param>
    /// <param name="fileName">Filename hint whose extension tells a remote provider how to decode the bytes.</param>
    /// <param name="contentType">The clip's MIME type (used to derive the filename when none is given).</param>
    /// <param name="applyCorrection">When true, run the validated dictionary corrector on the raw text.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GatewayTranscriptionResult> TranscribeAsync(
        byte[] audio, string fileName, string contentType, bool applyCorrection, CancellationToken ct)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));

        var routing = Resolve();
        var mode = "devthrottle";

        if (audio.Length == 0)
            return GatewayTranscriptionResult.NoAudio(mode, routing.Endpoint.Model);

        if (routing.Key is null)
            return GatewayTranscriptionResult.NoKey(mode, routing.Endpoint.Model);

        string raw;
        try
        {
            raw = await TranscribeRawCoreAsync(routing, audio, fileName, contentType, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InsufficientCreditsException ex)
        {
            // Out of credits (issue #885): a distinct, expected condition. Carry it as its own value so
            // the endpoint maps it to 402 and the client preserves the recording and offers "Add
            // credits" - never a raw error, never a lost clip.
            FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync OUT OF CREDITS: mode={mode}, code={ex.Code}");
            return GatewayTranscriptionResult.OutOfCredits(mode, routing.Endpoint.Model, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // A provider rejection (bad key, model not found, network) is an expected external
            // failure (CodingStyle: Result Objects for Expected Failures): carry it as a value so the
            // single endpoint maps it to 502, rather than throwing the same shape from N call sites.
            FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync provider FAILED: mode={mode}, {ex.Message}");
            return GatewayTranscriptionResult.ProviderError(mode, routing.Endpoint.Model, ex.Message);
        }

        var text = applyCorrection ? (await CleanupCoreAsync(routing, raw, ct)).Text : raw;
        FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync OK: mode={mode}, corrected={applyCorrection}, chars={text.Length}");
        return GatewayTranscriptionResult.Ok(text, mode, routing.Endpoint.Model);
    }

    /// <summary>
    /// Transcribe one complete audio segment to RAW text (no dictionary correction) using the
    /// configured mode. This is the phone Notes per-segment path: it batch-transcribes each segment,
    /// then runs the dictionary corrector ONCE on the assembled concatenation (<see cref="CleanupAsync"/>),
    /// so the assembled transcript is provably the per-segment raw concatenation plus dictionary edits
    /// only. Throws when no key is set for the selected remote mode, which the ingest worker records as
    /// a retryable transcription failure - never a guessed URL.
    /// </summary>
    public async Task<string> TranscribeSegmentRawAsync(
        byte[] audio, string fileName, string contentType, CancellationToken ct = default)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (audio.Length == 0) return "";

        var routing = Resolve();
        if (routing.Key is null)
            throw new InvalidOperationException(
                "No transcription method is available: no key is set for the selected transcription mode. "
                + "Set the key in the Cockpit Settings > Transcription tab.");

        return await TranscribeRawCoreAsync(routing, audio, fileName, contentType, ct);
    }

    /// <summary>
    /// Apply the validated dictionary correction to an assembled raw transcript and return the
    /// outcome. Fails open (CodingStyle section 16 / issue #190): with no key, on an empty dictionary,
    /// or on any cleanup error, the raw transcript comes back byte-identical.
    /// </summary>
    public async Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");
        return await CleanupCoreAsync(Resolve(), rawTranscript, ct);
    }

    /// <summary>
    /// Run the transcription provider for the resolved routing: ONE batch POST to the resolved
    /// OpenAI-compatible endpoint for the mode. Throws on a provider error (a missing transcript is a
    /// real failure the caller must surface).
    /// </summary>
    private async Task<string> TranscribeRawCoreAsync(
        GatewayTranscriptionRouting routing, byte[] audio, string fileName, string contentType, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "audio." + ExtensionFor(contentType) : fileName;
        using var pipeline = new BatchTranscriptionPipeline(httpClient: _http, cleanupModel: _cleanupModel);
        FileLog.Write($"[GatewayTranscriptionService] transcribe remote: bytes={audio.Length}, model={routing.Endpoint.Model}");
        return await pipeline.TranscribeRawAsync(audio, name, routing.ToResolved(), ct);
    }

    /// <summary>
    /// The validated dictionary correction core, shared by the optional <c>correct</c> flag and the
    /// Notes assemble-then-clean path. Fails open to the raw transcript.
    /// </summary>
    private async Task<CleanupOutcome> CleanupCoreAsync(
        GatewayTranscriptionRouting routing, string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new CleanupOutcome(raw ?? "", Applied: false, Reason: "empty transcript");

        // A missing key has no chat-completions endpoint for the corrector to reach - ship the raw
        // words, exactly as they stood before this step existed.
        if (routing.Key is null)
        {
            FileLog.Write("[GatewayTranscriptionService] cleanup: no-key - shipping raw");
            return new CleanupOutcome(raw, Applied: false, Reason: "no-key: no corrector endpoint");
        }

        // CleanAsync short-circuits an empty dictionary to a verbatim passthrough; the early check just
        // avoids constructing the orchestrator for nothing.
        var dictionary = _dictionaryProvider();
        if (dictionary.Vocabulary.Count == 0 && dictionary.CommonMistranscriptions.Count == 0)
            return new CleanupOutcome(raw, Applied: false, Reason: "empty dictionary");

        using var cleanup = new CleanupOrchestrator(
            apiKey: routing.Key, model: _cleanupModel, httpClient: _http, baseUrl: routing.Endpoint.RequireBaseUrl());
        var outcome = await cleanup.CleanAsync(raw, dictionary, "default", ct);
        FileLog.Write($"[GatewayTranscriptionService] cleanup: applied={outcome.Applied}, changed={outcome.ChangedWords.Count}, reason=\"{outcome.Reason}\"");
        return outcome;
    }

    /// <summary>
    /// The single shared dictation glossary file used by both the recording transcriber and the
    /// dictionary editor endpoints, so the path is defined in exactly one place.
    /// </summary>
    public static string DictionaryPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");
    }

    /// <summary>The file extension (no dot) for an audio MIME type, used to name the upload so a
    /// remote provider decodes the bytes correctly. Defaults to <c>webm</c> (what browsers record).</summary>
    public static string ExtensionFor(string? contentType)
    {
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return ct switch
        {
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            _ => "webm",
        };
    }
}

/// <summary>
/// The resolved transcription routing for the configured mode (issue #839): the pure
/// <see cref="TranscriptionEndpoint"/> plus the key read from the vault (null when no key is set for
/// the mode). Produced by the single resolver, <see cref="GatewayTranscriptionService.Resolve"/>.
/// </summary>
public sealed record GatewayTranscriptionRouting
{
    /// <summary>The pure routing target (base URL, key name, model).</summary>
    public required TranscriptionEndpoint Endpoint { get; init; }

    /// <summary>The vault key, or null when no key is set.</summary>
    public string? Key { get; init; }

    /// <summary>
    /// Compose the <see cref="ResolvedTranscription"/> the remote batch pipeline consumes. Throws when
    /// no key is set - call only after checking <see cref="Key"/> is non-null.
    /// </summary>
    public ResolvedTranscription ToResolved()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new InvalidOperationException("no DevThrottle key set for transcription");
        return new ResolvedTranscription
        {
            BaseUrl = Endpoint.RequireBaseUrl(),
            ApiKey = Key,
            Model = Endpoint.Model,
        };
    }
}

/// <summary>How a <see cref="GatewayTranscriptionService.TranscribeAsync"/> call ended.</summary>
public enum TranscriptionOutcome
{
    /// <summary>Transcription succeeded; <see cref="GatewayTranscriptionResult.Text"/> is the text.</summary>
    Ok = 0,

    /// <summary>The request carried no audio bytes.</summary>
    NoAudio = 1,

    /// <summary>No key is set for the current remote transcription mode.</summary>
    NoKey = 2,

    /// <summary>The provider rejected the request or the key.</summary>
    ProviderError = 3,

    /// <summary>The DevThrottle account is out of credits (HTTP 402) - issue #885.</summary>
    OutOfCredits = 4,
}

/// <summary>
/// The outcome of a single audio-to-text call (issue #839): the outcome kind, the text when it
/// succeeded, the mode and model that ran, and the error when it did not.
/// </summary>
public sealed record GatewayTranscriptionResult(
    TranscriptionOutcome Outcome, string? Text, string Mode, string? Model, string? Error, string? Code = null)
{
    public static GatewayTranscriptionResult Ok(string text, string mode, string? model)
        => new(TranscriptionOutcome.Ok, text, mode, model, null);

    public static GatewayTranscriptionResult NoAudio(string mode, string? model)
        => new(TranscriptionOutcome.NoAudio, null, mode, model, "no audio in the request body");

    public static GatewayTranscriptionResult NoKey(string mode, string? model)
        => new(TranscriptionOutcome.NoKey, null, mode, model, "no key set for the current transcription mode");

    public static GatewayTranscriptionResult ProviderError(string mode, string? model, string error)
        => new(TranscriptionOutcome.ProviderError, null, mode, model, error);

    /// <summary>Out of credits (issue #885): carries the hosted service's error code so the client can
    /// show the add-credits state and keep the recording.</summary>
    public static GatewayTranscriptionResult OutOfCredits(string mode, string? model, string code, string error)
        => new(TranscriptionOutcome.OutOfCredits, null, mode, model, error, code);
}
