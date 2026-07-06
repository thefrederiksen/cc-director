namespace CcDirector.Core.Configuration;

/// <summary>
/// HOW the dictation pipeline talks to the transcription provider (issue #513). Different
/// providers expose different transports, and the pipeline must honor the one the routing names -
/// it can NEVER open a transport a provider does not offer.
///
///   - <see cref="Realtime"/>: OpenAI's Realtime transcription WebSocket
///     (<c>wss://api.openai.com/v1/realtime?intent=transcription</c>). True low-latency partials.
///     Only OpenAI offers it, so it is the BYO/OpenAI transport.
///   - <see cref="Batch"/>: the OpenAI-COMPATIBLE batch endpoint
///     (<c>POST /audio/transcriptions</c>). Record a speech chunk, upload it, get text back.
///     This is what the DevThrottle proxy (Groq Whisper) implements - and the ONLY thing it
///     implements; Groq has no Realtime API. So it is the DevThrottle transport.
/// </summary>
public enum TranscriptionTransport
{
    /// <summary>OpenAI Realtime transcription WebSocket. The BYO/OpenAI transport.</summary>
    Realtime = 0,

    /// <summary>OpenAI-compatible batch <c>/audio/transcriptions</c>. The DevThrottle transport.</summary>
    Batch = 1,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionTransport"/>. Pure - unit-tested.</summary>
public static class TranscriptionTransportExtensions
{
    /// <summary>The wire/config string form: "realtime" or "batch".</summary>
    public static string ToConfigString(this TranscriptionTransport transport) => transport switch
    {
        TranscriptionTransport.Realtime => "realtime",
        TranscriptionTransport.Batch => "batch",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown transcription transport"),
    };

    /// <summary>
    /// Parse a wire value. Any unrecognized value (including null/empty/whitespace) THROWS with the
    /// allowed set named (no-fallback rule: a typo or a missing field must not silently pick a
    /// transport, which would let the pipeline open the wrong wire to the wrong provider).
    /// </summary>
    public static TranscriptionTransport Parse(string? value)
    {
        return (value?.Trim().ToLowerInvariant()) switch
        {
            "realtime" => TranscriptionTransport.Realtime,
            "batch" => TranscriptionTransport.Batch,
            _ => throw new ArgumentException(
                $"transport '{value}' is not valid - it must be \"realtime\" or \"batch\".", nameof(value)),
        };
    }
}

/// <summary>
/// The resolved transcription target for a <see cref="TranscriptionMode"/> (issue #497, #887): which
/// base URL the OpenAI-compatible transcription client points at, which vault key name holds the
/// credential it presents, which transport the pipeline must use, and which model. Pure, immutable,
/// unit-tested - this is the single place that decides routing, so the security-critical rule
/// ("the bring-your-own OpenAI key is NEVER sent to devthrottle.com") is provable in one spot.
///
/// Both modes are remote and key-bearing (issue #887 removed the in-process local option), so
/// <see cref="BaseUrl"/> and <see cref="KeyName"/> are always present.
/// </summary>
public sealed record TranscriptionEndpoint
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The vault key name that holds the credential for this mode.</summary>
    public required string KeyName { get; init; }

    /// <summary>
    /// The transport the dictation pipeline must use for this mode (issue #513). Part of the routing
    /// target so the pipeline never opens a wire the provider does not offer - the DevThrottle hosted
    /// path is batch-only and BYO/OpenAI is realtime. Pinned in the same pure spot that pins the URL
    /// and model.
    /// </summary>
    public required TranscriptionTransport Transport { get; init; }

    /// <summary>
    /// The transcription model this mode uses - provider-correct (issue #513):
    /// <c>gpt-4o-transcribe</c> for BYO/OpenAI and <c>whisper-large-v3</c> for the DevThrottle hosted
    /// path (which returns 404 model_not_found for gpt-4o-transcribe). Part of the routing target so
    /// the Gateway serves the full pair in one call (issue #506) - the same pure spot that pins the
    /// URL also pins the model.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>The mode this endpoint was resolved for.</summary>
    public required TranscriptionMode Mode { get; init; }

    /// <summary>True when this endpoint targets DevThrottle's hosted transcription.</summary>
    public bool IsDevThrottle => Mode == TranscriptionMode.DevThrottle;

    /// <summary>
    /// The vault key name for this mode. Both modes are key-bearing (issue #887), so this always
    /// returns the name; kept as a method so existing call sites compile unchanged.
    /// </summary>
    public string RequireKeyName() => KeyName;

    /// <summary>
    /// The remote base URL for this mode. Both modes are remote (issue #887), so this always returns
    /// the URL; kept as a method so existing call sites compile unchanged.
    /// </summary>
    public string RequireBaseUrl() => BaseUrl;
}

/// <summary>
/// Maps a <see cref="TranscriptionMode"/> to its <see cref="TranscriptionEndpoint"/> and validates
/// the key formats. Stateless and pure so the routing decision is fully unit-testable.
/// </summary>
public static class TranscriptionEndpointResolver
{
    /// <summary>OpenAI-compatible base URL used by the bring-your-own-key path.</summary>
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    /// <summary>OpenAI-compatible base URL used by the DevThrottle managed proxy path.</summary>
    public const string DevThrottleBaseUrl = "https://devthrottle.com/api/v1";

    /// <summary>Vault key name for the user's own OpenAI key (the bring-your-own-key mode).</summary>
    public const string OpenAiKeyName = "OPENAI_API_KEY";

    /// <summary>Vault key name for the DevThrottle-issued key (the DevThrottle mode).</summary>
    public const string DevThrottleKeyName = "DEVTHROTTLE_API_KEY";

    /// <summary>
    /// The BYO/OpenAI transcription model. Matches <c>OpenAiRealtimeProvider.DefaultModel</c> /
    /// <c>OpenAiTranscriptionProvider.DefaultModel</c>; carried in the routing target the Gateway
    /// serves (issue #506). Named "Default" for back-compat with the pre-#513 shared constant.
    /// </summary>
    public const string DefaultModel = "gpt-4o-transcribe";

    /// <summary>The OpenAI/BYO transcription model (alias of <see cref="DefaultModel"/>, issue #513).</summary>
    public const string OpenAiModel = DefaultModel;

    /// <summary>
    /// The DevThrottle/Groq transcription model (issue #513). The DevThrottle batch Whisper proxy
    /// serves <c>whisper-large-v3</c> and returns 404 model_not_found for <c>gpt-4o-transcribe</c>,
    /// so DevThrottle mode must carry this provider-correct model - never the shared OpenAI default.
    /// </summary>
    public const string DevThrottleModel = "whisper-large-v3";

    /// <summary>
    /// The DEFAULT wingman (voice-summary) chat model per provider. The wingman is a stateless
    /// OpenAI-compatible <c>/chat/completions</c> call to the selected provider's base URL. These are
    /// ids the provider's catalog actually serves (verified live): the DevThrottle proxy uses full ids
    /// like <c>zai-org/GLM-5.2</c> (the short alias <c>glm-5.2</c> is rejected), OpenAI uses
    /// <c>gpt-5.5</c>. The user can pick any model from the live catalog; this is only the default.
    /// </summary>
    public const string DevThrottleWingmanModel = "zai-org/GLM-5.2";

    /// <summary>The default OpenAI wingman chat model (bring-your-own key). See <see cref="DevThrottleWingmanModel"/>.</summary>
    public const string OpenAiWingmanModel = "gpt-5.5";

    /// <summary>
    /// The DEFAULT fast wingman chat model per provider. Fast wingman calls are response-only tasks
    /// like spoken turn summaries, menu detection, and choice mapping where latency matters more than
    /// deeper reasoning.
    /// </summary>
    public const string DevThrottleWingmanFastModel = "Qwen/Qwen2.5-72B-Instruct";

    /// <summary>The default OpenAI fast wingman chat model. See <see cref="DevThrottleWingmanFastModel"/>.</summary>
    public const string OpenAiWingmanFastModel = "gpt-5.5-mini";

    /// <summary>
    /// The DEFAULT text-to-speech model per provider. The catalogs differ (verified live): the
    /// DevThrottle proxy serves open TTS models like <c>hexgrad/Kokoro-82M</c> (it does NOT serve
    /// OpenAI's <c>tts-1</c>), OpenAI serves <c>tts-1</c>. The user can pick any speech model from the
    /// live catalog; this is only the default.
    /// </summary>
    public const string DevThrottleTtsModel = "hexgrad/Kokoro-82M";

    /// <summary>The default OpenAI text-to-speech model. See <see cref="DevThrottleTtsModel"/>.</summary>
    public const string OpenAiTtsModel = "tts-1";

    /// <summary>The DEFAULT voice per provider. Kokoro's own default is <c>af_bella</c>; OpenAI's is
    /// <c>nova</c>. Voices are provider-specific (a Kokoro voice is not an OpenAI voice), so switching
    /// providers must reset the voice - the two sets do not overlap.</summary>
    public const string DevThrottleTtsVoice = "af_bella";

    /// <summary>The default OpenAI voice. See <see cref="DevThrottleTtsVoice"/>.</summary>
    public const string OpenAiTtsVoice = "nova";

    /// <summary>The default speech model for <paramref name="mode"/> (see <see cref="ResolveTts"/>.Model).</summary>
    public static string DefaultTtsModel(TranscriptionMode mode) => ResolveTts(mode).Model;

    /// <summary>The default voice for <paramref name="mode"/>.</summary>
    public static string DefaultTtsVoice(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => OpenAiTtsVoice,
        TranscriptionMode.DevThrottle => DevThrottleTtsVoice,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Resolve the wingman chat-completions target for <paramref name="mode"/> (base URL + key name +
    /// model). Same base + key as transcription; the caller appends <c>/chat/completions</c>.
    /// </summary>
    public static ProviderEndpoint ResolveWingman(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new ProviderEndpoint(OpenAiBaseUrl, OpenAiKeyName, OpenAiWingmanModel),
        TranscriptionMode.DevThrottle => new ProviderEndpoint(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanModel),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Resolve the fast wingman chat-completions target for <paramref name="mode"/>. Same base URL and
    /// key as <see cref="ResolveWingman"/>; only the default model differs.
    /// </summary>
    public static ProviderEndpoint ResolveWingmanFast(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new ProviderEndpoint(OpenAiBaseUrl, OpenAiKeyName, OpenAiWingmanFastModel),
        TranscriptionMode.DevThrottle => new ProviderEndpoint(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanFastModel),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Resolve the text-to-speech target for <paramref name="mode"/> (base URL + key name + model).
    /// Same base + key as transcription; the caller appends <c>/audio/speech</c>. The voice is a
    /// separate choice (<see cref="TtsVoiceConfig"/>), not part of the routing target.
    /// </summary>
    public static ProviderEndpoint ResolveTts(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new ProviderEndpoint(OpenAiBaseUrl, OpenAiKeyName, OpenAiTtsModel),
        TranscriptionMode.DevThrottle => new ProviderEndpoint(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleTtsModel),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>Resolve the routing target for <paramref name="mode"/> (URL + key + transport + model).</summary>
    public static TranscriptionEndpoint Resolve(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new TranscriptionEndpoint
        {
            BaseUrl = OpenAiBaseUrl,
            KeyName = OpenAiKeyName,
            Transport = TranscriptionTransport.Realtime,
            Model = OpenAiModel,
            Mode = TranscriptionMode.Byo,
        },
        TranscriptionMode.DevThrottle => new TranscriptionEndpoint
        {
            BaseUrl = DevThrottleBaseUrl,
            KeyName = DevThrottleKeyName,
            Transport = TranscriptionTransport.Batch,
            Model = DevThrottleModel,
            Mode = TranscriptionMode.DevThrottle,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// True when <paramref name="key"/> looks like a DevThrottle key (<c>dt_live_</c> or
    /// <c>dt_test_</c> prefix). Format-only - it does not verify the key works.
    /// </summary>
    public static bool IsValidDevThrottleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        return k.StartsWith("dt_live_", StringComparison.Ordinal)
            || k.StartsWith("dt_test_", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="key"/> looks like an OpenAI key (<c>sk-</c> prefix). Format-only -
    /// it does not verify the key works.
    /// </summary>
    public static bool IsValidOpenAiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return key.Trim().StartsWith("sk-", StringComparison.Ordinal);
    }
}

/// <summary>
/// An OpenAI-compatible provider target: the <c>/v1</c> base URL, the vault key name that holds the
/// credential, and the model. Used for the wingman chat-completions call and the text-to-speech call
/// (<see cref="TranscriptionEndpointResolver.ResolveWingman"/> / <see cref="TranscriptionEndpointResolver.ResolveTts"/>).
/// The caller appends the operation path (<c>/chat/completions</c> or <c>/audio/speech</c>).
/// </summary>
public sealed record ProviderEndpoint(string BaseUrl, string KeyName, string Model);
