namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for converting text to speech audio.
/// </summary>
public interface ITextToSpeech
{
    /// <summary>
    /// Synthesize text to audio file.
    /// </summary>
    /// <param name="text">The text to speak.</param>
    /// <param name="outputPath">Path for the output audio file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the TTS service is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }
}
