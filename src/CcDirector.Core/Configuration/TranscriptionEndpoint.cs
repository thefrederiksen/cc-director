namespace CcDirector.Core.Configuration;

/// <summary>
/// The resolved transcription target: which base URL the OpenAI-compatible transcription client
/// points at, which vault key name holds the credential it presents, and which model. Pure,
/// immutable, unit-tested - this is the single place that decides routing.
///
/// There is exactly ONE provider: DevThrottle's hosted service on the signed-in account. Every AI
/// capability (transcription, the wingman chat call, and text-to-speech) draws on the account's
/// credits through <c>https://devthrottle.com/api/v1</c> with the account's DevThrottle key - there
/// is no bring-your-own-provider option.
/// </summary>
public sealed record TranscriptionEndpoint
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://devthrottle.com/api/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The vault key name that holds the DevThrottle credential.</summary>
    public required string KeyName { get; init; }

    /// <summary>
    /// The transcription model this target uses - <c>whisper-large-v3</c>, the model the DevThrottle
    /// batch Whisper proxy serves. Part of the routing target so the Gateway serves the full pair in
    /// one call (issue #506).
    /// </summary>
    public required string Model { get; init; }

    /// <summary>The vault key name for this target. Kept as a method so existing call sites compile unchanged.</summary>
    public string RequireKeyName() => KeyName;

    /// <summary>The remote base URL for this target. Kept as a method so existing call sites compile unchanged.</summary>
    public string RequireBaseUrl() => BaseUrl;
}

/// <summary>
/// Resolves the one <see cref="TranscriptionEndpoint"/> (DevThrottle hosted) and its default models.
/// Stateless and pure so the routing decision is fully unit-testable.
/// </summary>
public static class TranscriptionEndpointResolver
{
    /// <summary>OpenAI-compatible base URL used by the DevThrottle managed proxy path.</summary>
    public const string DevThrottleBaseUrl = "https://devthrottle.com/api/v1";

    /// <summary>Vault key name for the DevThrottle-issued key.</summary>
    public const string DevThrottleKeyName = "DEVTHROTTLE_API_KEY";

    /// <summary>
    /// The DevThrottle/Groq transcription model. The DevThrottle batch Whisper proxy serves
    /// <c>whisper-large-v3</c>.
    /// </summary>
    public const string DevThrottleModel = "whisper-large-v3";

    /// <summary>
    /// The DEFAULT wingman (voice-summary) chat model. The wingman is a stateless OpenAI-compatible
    /// <c>/chat/completions</c> call to the DevThrottle proxy, which serves full ids like
    /// <c>zai-org/GLM-5.2</c> (the short alias <c>glm-5.2</c> is rejected). The user can pick any model
    /// from the live catalog; this is only the default.
    /// </summary>
    public const string DevThrottleWingmanModel = "zai-org/GLM-5.2";

    /// <summary>
    /// The DEFAULT fast wingman chat model. Fast wingman calls are response-only tasks like spoken turn
    /// summaries, menu detection, and choice mapping where latency matters more than deeper reasoning.
    /// </summary>
    public const string DevThrottleWingmanFastModel = "Qwen/Qwen2.5-72B-Instruct";

    /// <summary>
    /// The DEFAULT text-to-speech model. The DevThrottle proxy serves open TTS models like
    /// <c>hexgrad/Kokoro-82M</c>. The user can pick any speech model from the live catalog; this is
    /// only the default.
    /// </summary>
    public const string DevThrottleTtsModel = "hexgrad/Kokoro-82M";

    /// <summary>The DEFAULT voice - Kokoro's own default is <c>af_bella</c>.</summary>
    public const string DevThrottleTtsVoice = "af_bella";

    /// <summary>The default speech model (see <see cref="ResolveTts"/>.Model).</summary>
    public static string DefaultTtsModel() => DevThrottleTtsModel;

    /// <summary>The default voice.</summary>
    public static string DefaultTtsVoice() => DevThrottleTtsVoice;

    /// <summary>
    /// Resolve the wingman chat-completions target (base URL + key name + model). Same base + key as
    /// transcription; the caller appends <c>/chat/completions</c>.
    /// </summary>
    public static ProviderEndpoint ResolveWingman() =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanModel);

    /// <summary>
    /// Resolve the fast wingman chat-completions target. Same base URL and key as
    /// <see cref="ResolveWingman"/>; only the default model differs.
    /// </summary>
    public static ProviderEndpoint ResolveWingmanFast() =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanFastModel);

    /// <summary>
    /// Resolve the text-to-speech target (base URL + key name + model). Same base + key as
    /// transcription; the caller appends <c>/audio/speech</c>. The voice is a separate choice
    /// (<see cref="TtsVoiceConfig"/>), not part of the routing target.
    /// </summary>
    public static ProviderEndpoint ResolveTts() =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleTtsModel);

    /// <summary>Resolve the routing target (URL + key + model).</summary>
    public static TranscriptionEndpoint Resolve() => new()
    {
        BaseUrl = DevThrottleBaseUrl,
        KeyName = DevThrottleKeyName,
        Model = DevThrottleModel,
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
}

/// <summary>
/// An OpenAI-compatible provider target: the <c>/v1</c> base URL, the vault key name that holds the
/// credential, and the model. Used for the wingman chat-completions call and the text-to-speech call
/// (<see cref="TranscriptionEndpointResolver.ResolveWingman"/> / <see cref="TranscriptionEndpointResolver.ResolveTts"/>).
/// The caller appends the operation path (<c>/chat/completions</c> or <c>/audio/speech</c>).
/// </summary>
public sealed record ProviderEndpoint(string BaseUrl, string KeyName, string Model);
