namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for summarizing Claude responses into conversational form for TTS.
/// </summary>
public interface IResponseSummarizer
{
    /// <summary>
    /// Summarize a Claude response into a short, conversational form suitable for speech.
    /// </summary>
    /// <param name="response">The full Claude response text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A short, conversational summary (2-3 sentences).</returns>
    Task<string> SummarizeAsync(string response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the summarizer is available (e.g., Claude CLI is installed).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }
}
