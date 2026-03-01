using NAudio.Wave;

namespace VoiceChat.Core.Pipeline;

/// <summary>
/// Captures microphone audio using NAudio WaveInEvent.
/// Records 16kHz, 16-bit mono PCM (the format Whisper expects).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly WaveInEvent _waveIn;
    private MemoryStream? _buffer;
    private bool _isRecording;

    public static readonly WaveFormat CaptureFormat = new(16000, 16, 1);

    public bool IsRecording => _isRecording;

    public event Action<string>? StatusChanged;

    public AudioCapture()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = CaptureFormat,
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        _buffer = new MemoryStream();
        _isRecording = true;
        _waveIn.StartRecording();
        StatusChanged?.Invoke("Recording...");
    }

    public byte[] StopRecording()
    {
        if (!_isRecording) return [];

        _waveIn.StopRecording();
        _isRecording = false;

        var data = _buffer?.ToArray() ?? [];
        _buffer?.Dispose();
        _buffer = null;

        StatusChanged?.Invoke($"Captured {data.Length / 2} samples ({data.Length / 32000.0:F1}s)");
        return data;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        if (_isRecording)
        {
            _waveIn.StopRecording();
        }
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _buffer?.Dispose();
    }
}
