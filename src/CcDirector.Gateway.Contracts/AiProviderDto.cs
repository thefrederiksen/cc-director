namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The shape of the consolidated AI-provider endpoints: the body of <c>GET /gateway/ai-provider</c>
/// and the body returned by <c>PUT /gateway/ai-provider</c>. The Cockpit AI page deserializes this to
/// render the one provider switch (DevThrottle vs OpenAI) and show what it resolves to.
///
/// <see cref="Provider"/> is the projection of the stored transcription mode: "devthrottle" (the
/// hosted, account-billed provider - the default) or "openai" (the user's own OpenAI key). The models
/// are the provider-correct values the wingman and transcription actually use; the voice + selectable
/// set describe the text-to-speech picker.
/// </summary>
public sealed class AiProviderDto
{
    /// <summary>The selected provider: "devthrottle" or "openai".</summary>
    public string Provider { get; set; } = "devthrottle";

    /// <summary>The wingman (voice-summary) chat model for the provider (glm-5.2 or gpt-5.5).</summary>
    public string WingmanModel { get; set; } = "";

    /// <summary>The transcription (speech-to-text) model for the provider.</summary>
    public string TranscriptionModel { get; set; } = "";

    /// <summary>The selected text-to-speech voice.</summary>
    public string TtsVoice { get; set; } = "nova";

    /// <summary>The selectable text-to-speech voices for the picker.</summary>
    public List<string> Voices { get; set; } = new();
}

/// <summary>
/// The shape of the text-to-speech voice endpoints (<c>GET/PUT /gateway/tts-voice</c>): the selected
/// voice and the selectable set. Applies to whichever AI provider is chosen (both are OpenAI-compatible
/// for speech).
/// </summary>
public sealed class TtsVoiceDto
{
    /// <summary>The selected text-to-speech voice.</summary>
    public string Voice { get; set; } = "nova";

    /// <summary>The selectable voices for the picker.</summary>
    public List<string> Voices { get; set; } = new();
}
