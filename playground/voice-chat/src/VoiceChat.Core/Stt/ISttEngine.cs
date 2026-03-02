namespace VoiceChat.Core.Stt;

/// <summary>
/// Abstraction for speech-to-text engines (batch mode).
/// </summary>
public interface ISttEngine : IDisposable
{
    string DisplayName { get; }
    string Description { get; }
    bool IsReady { get; }

    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Transcribes 16-bit PCM audio at 16kHz mono.
    /// </summary>
    Task<string> TranscribeAsync(byte[] pcmAudio, CancellationToken ct = default);

    event Action<string>? StatusChanged;
}

/// <summary>
/// Extended interface for streaming STT engines that provide partial results
/// while audio is being recorded.
/// </summary>
public interface IStreamingSttEngine : ISttEngine
{
    void BeginStream();
    void FeedAudioChunk(byte[] buffer, int bytesRecorded);
    string EndStream();

    event Action<string>? PartialResultReady;
}
