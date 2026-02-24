using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Tests.Voice.Mocks;

/// <summary>
/// Mock text-to-speech for testing.
/// Creates an empty WAV file at the output path.
/// </summary>
public class MockTextToSpeech : ITextToSpeech
{
    private readonly bool _isAvailable;
    private readonly string? _unavailableReason;
    private readonly int _delayMs;

    public MockTextToSpeech(
        bool isAvailable = true,
        string? unavailableReason = null,
        int delayMs = 0)
    {
        _isAvailable = isAvailable;
        _unavailableReason = unavailableReason;
        _delayMs = delayMs;
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason => _unavailableReason;

    public int SynthesizeCallCount { get; private set; }
    public string? LastText { get; private set; }
    public string? LastOutputPath { get; private set; }

    public async Task SynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        SynthesizeCallCount++;
        LastText = text;
        LastOutputPath = outputPath;

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        // Create a minimal WAV file (44 bytes header + no audio data)
        var wavHeader = new byte[]
        {
            // RIFF header
            0x52, 0x49, 0x46, 0x46, // "RIFF"
            0x24, 0x00, 0x00, 0x00, // File size - 8 (36 bytes)
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
            0x00, 0x00, 0x00, 0x00, // Subchunk2Size (0 = no audio)
        };

        await File.WriteAllBytesAsync(outputPath, wavHeader, cancellationToken);
    }
}
