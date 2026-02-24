using System.IO;
using NAudio.Wave;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Wpf.Voice;

/// <summary>
/// Audio recorder using NAudio WaveInEvent.
/// Records 16kHz, 16-bit, mono WAV suitable for speech recognition.
/// </summary>
public class AudioRecorder : IAudioRecorder, IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _outputPath;
    private bool _disposed;
    private bool? _isAvailable;
    private string? _unavailableReason;
    private bool _isRecording;

    /// <inheritdoc />
    public bool IsRecording => _isRecording;

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _isAvailable!.Value;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _unavailableReason;
        }
    }

    /// <inheritdoc />
    public event Action<float>? OnLevelChanged;

    /// <inheritdoc />
    public event Action<byte[]>? OnAudioDataAvailable;

    /// <inheritdoc />
    public void StartRecording()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioRecorder));

        if (IsRecording)
            return;

        if (!IsAvailable)
            throw new InvalidOperationException(UnavailableReason ?? "Microphone not available");

        FileLog.Write("[AudioRecorder] StartRecording");

        _outputPath = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.wav");

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50
        };

        _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        _isRecording = true;
        FileLog.Write($"[AudioRecorder] Recording started: {_outputPath}");
    }

    /// <inheritdoc />
    public Task<string> StopRecordingAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioRecorder));

        FileLog.Write("[AudioRecorder] StopRecording");

        if (_waveIn == null)
            throw new InvalidOperationException("Not recording");

        var tcs = new TaskCompletionSource<string>();
        var outputPath = _outputPath!;

        void OnStopped(object? sender, StoppedEventArgs e)
        {
            FileLog.Write($"[AudioRecorder] Recording stopped: {outputPath}");
            tcs.TrySetResult(outputPath);
        }

        // Subscribe to stopped event before stopping
        _waveIn.RecordingStopped += OnStopped;
        _waveIn.StopRecording();

        // Cleanup after task completes
        tcs.Task.ContinueWith(_ =>
        {
            CleanupRecording();
        });

        return tcs.Task;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer == null) return;

        _writer.Write(e.Buffer, 0, e.BytesRecorded);

        // Calculate RMS level for visualization
        float max = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
            float sampleFloat = Math.Abs(sample / 32768f);
            if (sampleFloat > max)
                max = sampleFloat;
        }

        OnLevelChanged?.Invoke(max);

        // Fire audio data event for streaming transcription
        if (OnAudioDataAvailable != null && e.BytesRecorded > 0)
        {
            var audioData = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, audioData, e.BytesRecorded);
            OnAudioDataAvailable.Invoke(audioData);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            FileLog.Write($"[AudioRecorder] Recording error: {e.Exception.Message}");
        }
    }

    private void CleanupRecording()
    {
        _isRecording = false;
        _writer?.Dispose();
        _writer = null;

        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    private void CheckAvailability()
    {
        try
        {
            int deviceCount = WaveInEvent.DeviceCount;
            if (deviceCount == 0)
            {
                _isAvailable = false;
                _unavailableReason = "No microphone detected. Connect a microphone and try again.";
                FileLog.Write("[AudioRecorder] No recording devices found");
                return;
            }

            // Log available devices
            for (int i = 0; i < deviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                FileLog.Write($"[AudioRecorder] Device {i}: {caps.ProductName}");
            }

            _isAvailable = true;
            _unavailableReason = null;
            FileLog.Write($"[AudioRecorder] {deviceCount} recording device(s) available");
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _unavailableReason = $"Failed to check microphone: {ex.Message}";
            FileLog.Write($"[AudioRecorder] CheckAvailability FAILED: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
        {
            _waveIn?.StopRecording();
        }

        CleanupRecording();
    }
}
