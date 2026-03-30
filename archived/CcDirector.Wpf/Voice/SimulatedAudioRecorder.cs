using System.IO;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Wpf.Voice;

/// <summary>
/// Simulated audio recorder for testing without a real microphone.
/// Returns a pre-recorded or generated WAV file.
/// </summary>
public class SimulatedAudioRecorder : IAudioRecorder
{
    private readonly string? _preRecordedPath;
    private bool _isRecording;
    private string? _outputPath;

    /// <summary>
    /// Create a simulated recorder that returns a pre-recorded file.
    /// </summary>
    /// <param name="preRecordedPath">Path to an existing WAV file to return.</param>
    public SimulatedAudioRecorder(string? preRecordedPath = null)
    {
        _preRecordedPath = preRecordedPath;
    }

    /// <inheritdoc />
    public bool IsRecording => _isRecording;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <inheritdoc />
    public event Action<float>? OnLevelChanged;

    /// <inheritdoc />
    public event Action<byte[]>? OnAudioDataAvailable;

    /// <inheritdoc />
    public void StartRecording()
    {
        FileLog.Write("[SimulatedAudioRecorder] StartRecording");
        _isRecording = true;

        // Generate output path for mock WAV
        _outputPath = Path.Combine(Path.GetTempPath(), $"simulated_{Guid.NewGuid():N}.wav");

        // Simulate level changes and audio data
        Task.Run(async () =>
        {
            var random = new Random();
            while (_isRecording)
            {
                OnLevelChanged?.Invoke((float)random.NextDouble() * 0.5f + 0.2f);
                OnAudioDataAvailable?.Invoke(new byte[1600]); // Simulated audio chunk
                await Task.Delay(100);
            }
        });
    }

    /// <inheritdoc />
    public async Task<string> StopRecordingAsync()
    {
        FileLog.Write("[SimulatedAudioRecorder] StopRecording");
        _isRecording = false;

        // Simulate brief recording delay
        await Task.Delay(100);

        // If we have a pre-recorded file, return that
        if (!string.IsNullOrEmpty(_preRecordedPath) && File.Exists(_preRecordedPath))
        {
            FileLog.Write($"[SimulatedAudioRecorder] Returning pre-recorded: {_preRecordedPath}");
            return _preRecordedPath!;
        }

        // Otherwise create an empty WAV file
        if (_outputPath != null)
        {
            await CreateEmptyWavAsync(_outputPath);
            FileLog.Write($"[SimulatedAudioRecorder] Created mock WAV: {_outputPath}");
            return _outputPath;
        }

        throw new InvalidOperationException("StartRecording was not called");
    }

    private static async Task CreateEmptyWavAsync(string path)
    {
        // Minimal WAV header for 16kHz, 16-bit, mono
        var header = new byte[]
        {
            // RIFF header
            0x52, 0x49, 0x46, 0x46, // "RIFF"
            0x24, 0x00, 0x00, 0x00, // File size - 8
            0x57, 0x41, 0x56, 0x45, // "WAVE"
            // fmt subchunk
            0x66, 0x6D, 0x74, 0x20, // "fmt "
            0x10, 0x00, 0x00, 0x00, // Subchunk1Size (16)
            0x01, 0x00,             // AudioFormat (1 = PCM)
            0x01, 0x00,             // NumChannels (1 = mono)
            0x80, 0x3E, 0x00, 0x00, // SampleRate (16000)
            0x00, 0x7D, 0x00, 0x00, // ByteRate (32000)
            0x02, 0x00,             // BlockAlign (2)
            0x10, 0x00,             // BitsPerSample (16)
            // data subchunk
            0x64, 0x61, 0x74, 0x61, // "data"
            0x00, 0x00, 0x00, 0x00, // Subchunk2Size (0)
        };

        await File.WriteAllBytesAsync(path, header);
    }
}
