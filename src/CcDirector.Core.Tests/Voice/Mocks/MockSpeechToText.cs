using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Tests.Voice.Mocks;

/// <summary>
/// Mock speech-to-text for testing.
/// Returns a configurable hardcoded transcription.
/// </summary>
public class MockSpeechToText : ISpeechToText
{
    private readonly string _transcription;
    private readonly bool _isAvailable;
    private readonly string? _unavailableReason;
    private readonly int _delayMs;

    public MockSpeechToText(
        string transcription = "What files are in this directory?",
        bool isAvailable = true,
        string? unavailableReason = null,
        int delayMs = 0)
    {
        _transcription = transcription;
        _isAvailable = isAvailable;
        _unavailableReason = unavailableReason;
        _delayMs = delayMs;
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason => _unavailableReason;

    public int TranscribeCallCount { get; private set; }
    public string? LastAudioPath { get; private set; }

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        TranscribeCallCount++;
        LastAudioPath = audioPath;

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        return _transcription;
    }
}
