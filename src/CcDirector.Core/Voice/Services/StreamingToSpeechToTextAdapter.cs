using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Adapts an IStreamingSpeechToText to ISpeechToText.
/// Used when only streaming is available but batch interface is needed.
/// Note: This is a simple placeholder - actual transcription happens via streaming.
/// </summary>
public class StreamingToSpeechToTextAdapter : ISpeechToText
{
    private readonly IStreamingSpeechToText _streamingStt;

    public StreamingToSpeechToTextAdapter(IStreamingSpeechToText streamingStt)
    {
        _streamingStt = streamingStt;
    }

    public bool IsAvailable => _streamingStt.IsAvailable;

    public string? UnavailableReason => _streamingStt.UnavailableReason;

    public Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default)
    {
        // This adapter is used when streaming is primary.
        // In streaming mode, transcription happens via ProcessAudioChunk/EndSession.
        // This method would only be called as fallback, which shouldn't happen
        // when we have streaming enabled.
        // Return empty to indicate streaming should be used instead.
        return Task.FromResult(string.Empty);
    }

    public void Dispose()
    {
        // Don't dispose the streaming STT - it's managed by the controller
    }
}
