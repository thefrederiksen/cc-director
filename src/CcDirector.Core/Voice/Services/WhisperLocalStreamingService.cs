using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;
using Whisper.net;
using Whisper.net.Ggml;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Local streaming speech-to-text using Whisper.net (whisper.cpp wrapper).
/// Provides real-time transcription as audio is being recorded.
/// </summary>
public class WhisperLocalStreamingService : IStreamingSpeechToText
{
    private const int SampleRate = 16000;

    private readonly string _modelPath;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private List<float>? _audioBuffer;
    private string _currentTranscription = "";
    private bool _disposed;
    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// Default model directory.
    /// </summary>
    public static string DefaultModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CcDirector", "whisper-models");

    /// <summary>
    /// Create a WhisperLocalStreamingService with auto-detected model.
    /// </summary>
    public WhisperLocalStreamingService()
    {
        _modelPath = FindModelFile() ?? "";
    }

    /// <summary>
    /// Create a WhisperLocalStreamingService with a specific model path.
    /// </summary>
    /// <param name="modelPath">Path to the .bin model file.</param>
    public WhisperLocalStreamingService(string modelPath)
    {
        _modelPath = modelPath;
    }

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
    public string? ModelPath => _modelPath;

    /// <inheritdoc />
    public event Action<string>? OnPartialResult;

    /// <inheritdoc />
    public void StartSession()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WhisperLocalStreamingService));

        if (!IsAvailable)
            throw new InvalidOperationException($"Whisper not available: {UnavailableReason}");

        FileLog.Write($"[WhisperLocal] StartSession: model={_modelPath}");

        try
        {
            _factory = WhisperFactory.FromPath(_modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Environment.ProcessorCount > 4 ? 4 : Environment.ProcessorCount)
                .WithSegmentEventHandler(OnSegment)
                .Build();

            _audioBuffer = new List<float>();
            _currentTranscription = "";

            FileLog.Write("[WhisperLocal] Session started");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WhisperLocal] StartSession FAILED: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public void ProcessAudioChunk(byte[] audioData)
    {
        if (_disposed || _audioBuffer == null)
            return;

        // Convert 16-bit PCM to float samples
        var samples = ConvertToFloat(audioData);
        _audioBuffer.AddRange(samples);

        // Process every ~1 second of audio (16000 samples)
        if (_audioBuffer.Count >= SampleRate)
        {
            ProcessCurrentBuffer();
        }
    }

    /// <inheritdoc />
    public string EndSession()
    {
        FileLog.Write("[WhisperLocal] EndSession");

        if (_audioBuffer != null && _audioBuffer.Count > 0)
        {
            // Process remaining audio
            ProcessCurrentBuffer();
        }

        var result = _currentTranscription.Trim();
        FileLog.Write($"[WhisperLocal] Final transcription: {result}");

        // Cleanup
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _audioBuffer = null;

        return result;
    }

    private void ProcessCurrentBuffer()
    {
        if (_processor == null || _audioBuffer == null || _audioBuffer.Count == 0)
            return;

        try
        {
            var samples = _audioBuffer.ToArray();
            _audioBuffer.Clear();

            // Process synchronously
            _processor.Process(samples);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WhisperLocal] ProcessCurrentBuffer error: {ex.Message}");
        }
    }

    private void OnSegment(SegmentData segment)
    {
        var text = segment.Text.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        _currentTranscription += " " + text;
        FileLog.Write($"[WhisperLocal] Segment: {text}");

        OnPartialResult?.Invoke(_currentTranscription.Trim());
    }

    private static float[] ConvertToFloat(byte[] audioData)
    {
        var samples = new float[audioData.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)((audioData[i * 2 + 1] << 8) | audioData[i * 2]);
            samples[i] = sample / 32768f;
        }
        return samples;
    }

    private void CheckAvailability()
    {
        if (string.IsNullOrEmpty(_modelPath))
        {
            _isAvailable = false;
            _unavailableReason = $"No Whisper model found. Download a model to: {DefaultModelDir}";
            FileLog.Write("[WhisperLocal] No model file found");
            return;
        }

        if (!File.Exists(_modelPath))
        {
            _isAvailable = false;
            _unavailableReason = $"Model file not found: {_modelPath}";
            FileLog.Write($"[WhisperLocal] Model file does not exist: {_modelPath}");
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
        FileLog.Write($"[WhisperLocal] Available with model: {_modelPath}");
    }

    private static string? FindModelFile()
    {
        var modelNames = new[]
        {
            "ggml-base.en.bin",
            "ggml-small.en.bin",
            "ggml-tiny.en.bin",
            "ggml-base.bin",
            "ggml-small.bin",
            "ggml-tiny.bin"
        };

        var searchDirs = new[]
        {
            DefaultModelDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisper.cpp", "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "whisper.cpp", "models"),
            @"C:\whisper\models"
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var model in modelNames)
            {
                var path = Path.Combine(dir, model);
                if (File.Exists(path))
                {
                    FileLog.Write($"[WhisperLocal] Found model: {path}");
                    return path;
                }
            }

            // Also check for any .bin file
            var binFiles = Directory.GetFiles(dir, "ggml-*.bin");
            if (binFiles.Length > 0)
            {
                FileLog.Write($"[WhisperLocal] Found model: {binFiles[0]}");
                return binFiles[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Download a Whisper model if not present.
    /// </summary>
    /// <param name="modelType">Model type (tiny, base, small, medium, large).</param>
    /// <param name="progress">Progress callback (MB downloaded).</param>
    /// <returns>Path to the downloaded model.</returns>
    public static async Task<string> DownloadModelAsync(
        GgmlType modelType = GgmlType.Base,
        Action<int>? progress = null)
    {
        Directory.CreateDirectory(DefaultModelDir);

        var modelName = $"ggml-{modelType.ToString().ToLower()}.en.bin";
        var modelPath = Path.Combine(DefaultModelDir, modelName);

        if (File.Exists(modelPath))
        {
            FileLog.Write($"[WhisperLocal] Model already exists: {modelPath}");
            return modelPath;
        }

        FileLog.Write($"[WhisperLocal] Downloading model: {modelType}");

        using var httpClient = new System.Net.Http.HttpClient();
        var downloader = new WhisperGgmlDownloader(httpClient);
        using var modelStream = await downloader.GetGgmlModelAsync(modelType);
        using var fileStream = File.Create(modelPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await modelStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;

            // Report progress (approximate, since we don't know total size)
            progress?.Invoke((int)(totalRead / 1024 / 1024)); // MB downloaded
        }

        FileLog.Write($"[WhisperLocal] Model downloaded: {modelPath}");
        return modelPath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processor?.Dispose();
        _factory?.Dispose();
    }
}
