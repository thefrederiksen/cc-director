namespace CcDirector.Core.Voice.Models;

/// <summary>
/// States for the voice mode controller.
/// </summary>
public enum VoiceState
{
    /// <summary>Voice mode is idle, waiting for user to start recording.</summary>
    Idle,

    /// <summary>Recording audio from microphone.</summary>
    Recording,

    /// <summary>Transcribing recorded audio to text.</summary>
    Transcribing,

    /// <summary>Waiting for Claude to process the prompt.</summary>
    WaitingForClaude,

    /// <summary>Summarizing Claude's response for speech.</summary>
    Summarizing,

    /// <summary>Speaking the summary through TTS.</summary>
    Speaking,

    /// <summary>Voice mode encountered an error.</summary>
    Error
}
