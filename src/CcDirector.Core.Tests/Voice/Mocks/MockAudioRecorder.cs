using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Tests.Voice.Mocks;

/// <summary>
/// Mock audio recorder for testing.
/// Returns a path to a mock WAV file.
/// </summary>
public class MockAudioRecorder : IAudioRecorder
{
    private readonly bool _isAvailable;
    private readonly string? _unavailableReason;
    private readonly int _recordingDurationMs;
    private bool _isRecording;
    private string? _outputPath;

    public MockAudioRecorder(
        bool isAvailable = true,
        string? unavailableReason = null,
        int recordingDurationMs = 100)
    {
        _isAvailable = isAvailable;
        _unavailableReason = unavailableReason;
        _recordingDurationMs = recordingDurationMs;
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason => _unavailableReason;
    public bool IsRecording => _isRecording;

    public int StartRecordingCallCount { get; private set; }
    public int StopRecordingCallCount { get; private set; }

    public event Action<float>? OnLevelChanged;
    public event Action<byte[]>? OnAudioDataAvailable;

    public void StartRecording()
    {
        if (!_isAvailable)
            throw new InvalidOperationException(UnavailableReason ?? "Recorder not available");

        StartRecordingCallCount++;
        _isRecording = true;

        // Create temp file for output
        _outputPath = Path.Combine(Path.GetTempPath(), $"mock_recording_{Guid.NewGuid():N}.wav");

        // Simulate level changes and audio data
        OnLevelChanged?.Invoke(0.5f);
        OnAudioDataAvailable?.Invoke(new byte[0]);
    }

    public async Task<string> StopRecordingAsync()
    {
        StopRecordingCallCount++;
        _isRecording = false;

        // Simulate recording duration
        if (_recordingDurationMs > 0)
            await Task.Delay(_recordingDurationMs);

        // Create a mock WAV file
        if (_outputPath != null)
        {
            var wavHeader = CreateWavHeader(0);
            await File.WriteAllBytesAsync(_outputPath, wavHeader);
            return _outputPath;
        }

        throw new InvalidOperationException("StartRecording was not called");
    }

    private static byte[] CreateWavHeader(int dataSize)
    {
        return new byte[]
        {
            // RIFF header
            0x52, 0x49, 0x46, 0x46, // "RIFF"
            (byte)(36 + dataSize), (byte)((36 + dataSize) >> 8), 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45, // "WAVE"
            // fmt subchunk
            0x66, 0x6D, 0x74, 0x20, // "fmt "
            0x10, 0x00, 0x00, 0x00, // Subchunk1Size (16 for PCM)
            0x01, 0x00,             // AudioFormat (1 = PCM)
            0x01, 0x00,             // NumChannels (1 = mono)
            0x80, 0x3E, 0x00, 0x00, // SampleRate (16000)
            0x00, 0x7D, 0x00, 0x00, // ByteRate (32000)
            0x02, 0x00,             // BlockAlign (2)
            0x10, 0x00,             // BitsPerSample (16)
            // data subchunk
            0x64, 0x61, 0x74, 0x61, // "data"
            (byte)dataSize, (byte)(dataSize >> 8), 0x00, 0x00,
        };
    }
}
