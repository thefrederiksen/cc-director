namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for converting speech audio to text.
/// </summary>
public interface ISpeechToText
{
    /// <summary>
    /// Transcribe audio file to text.
    /// </summary>
    /// <param name="audioPath">Path to the audio file (WAV format, 16kHz mono preferred).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcribed text.</returns>
    Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the STT service is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }
}
