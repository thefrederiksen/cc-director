namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for real-time streaming speech-to-text.
/// Transcribes audio as it's being recorded.
/// </summary>
public interface IStreamingSpeechToText : IDisposable
{
    /// <summary>
    /// Start a new transcription session.
    /// </summary>
    void StartSession();

    /// <summary>
    /// Process an audio chunk and get partial transcription.
    /// </summary>
    /// <param name="audioData">Raw PCM audio data (16kHz, 16-bit, mono).</param>
    void ProcessAudioChunk(byte[] audioData);

    /// <summary>
    /// End the session and get the final transcription.
    /// </summary>
    /// <returns>The complete transcription.</returns>
    string EndSession();

    /// <summary>
    /// Fires when partial transcription is available.
    /// </summary>
    event Action<string>? OnPartialResult;

    /// <summary>
    /// Whether the streaming STT service is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Path to the model file being used.
    /// </summary>
    string? ModelPath { get; }
}
