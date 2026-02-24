namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for recording audio from microphone.
/// </summary>
public interface IAudioRecorder
{
    /// <summary>
    /// Start recording audio.
    /// </summary>
    void StartRecording();

    /// <summary>
    /// Stop recording and return the path to the recorded WAV file.
    /// </summary>
    /// <returns>Path to the recorded WAV file.</returns>
    Task<string> StopRecordingAsync();

    /// <summary>
    /// Whether recording is currently in progress.
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// Whether the recorder is available (microphone present).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Fires when recording level changes (for UI visualization).
    /// Value is 0.0 to 1.0.
    /// </summary>
    event Action<float>? OnLevelChanged;

    /// <summary>
    /// Fires when audio data is available (for streaming transcription).
    /// Data is raw PCM (16kHz, 16-bit, mono).
    /// </summary>
    event Action<byte[]>? OnAudioDataAvailable;
}
