using Whisper.net;
using Whisper.net.Ggml;
using VoiceChat.Core.Logging;

namespace VoiceChat.Core.Stt;

/// <summary>
/// Whisper.net-based speech-to-text engine.
/// Downloads and caches the GGML model, then transcribes audio buffers.
/// </summary>
public sealed class WhisperSttEngine : ISttEngine
{
    private readonly GgmlType _modelSize;
    private readonly string _modelsDir;
    private WhisperProcessor? _processor;

    public string DisplayName { get; }
    public string Description { get; }
    public bool IsReady => _processor is not null;

    public event Action<string>? StatusChanged;

    public WhisperSttEngine(GgmlType modelSize)
    {
        _modelSize = modelSize;

        var sizeName = modelSize.ToString();
        DisplayName = $"Whisper {sizeName}";
        Description = modelSize switch
        {
            GgmlType.Tiny => "~75 MB | Fast, lower accuracy",
            GgmlType.Base => "~142 MB | Medium speed and accuracy",
            GgmlType.Small => "~466 MB | Slow, high accuracy",
            _ => $"whisper.net {sizeName}",
        };

        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "voice-chat", "models", "whisper");
        Directory.CreateDirectory(_modelsDir);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        VoiceLog.Write($"[WhisperSttEngine] InitializeAsync: model={_modelSize}");

        var modelFileName = $"ggml-{_modelSize.ToString().ToLowerInvariant()}.bin";
        var modelPath = Path.Combine(_modelsDir, modelFileName);

        if (!File.Exists(modelPath))
        {
            StatusChanged?.Invoke($"Downloading Whisper {_modelSize} model...");
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(_modelSize, cancellationToken: ct);
            using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream, ct);
            StatusChanged?.Invoke($"Whisper {_modelSize} model downloaded.");
        }

        StatusChanged?.Invoke($"Loading Whisper {_modelSize} model...");
        var factory = WhisperFactory.FromPath(modelPath);
        _processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        VoiceLog.Write($"[WhisperSttEngine] InitializeAsync: {_modelSize} ready.");
        StatusChanged?.Invoke($"Whisper {_modelSize} ready.");
    }

    /// <summary>
    /// Transcribes 16-bit PCM audio at 16kHz mono.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcmAudio, CancellationToken ct = default)
    {
        if (_processor is null)
            throw new InvalidOperationException("WhisperSttEngine not initialized. Call InitializeAsync first.");

        // Write as WAV to a memory stream (Whisper.net expects a stream)
        using var wavStream = new MemoryStream();
        using (var writer = new BinaryWriter(wavStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // WAV header
            writer.Write("RIFF"u8);
            writer.Write(36 + pcmAudio.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);           // chunk size
            writer.Write((short)1);     // PCM format
            writer.Write((short)1);     // mono
            writer.Write(16000);        // sample rate
            writer.Write(32000);        // byte rate
            writer.Write((short)2);     // block align
            writer.Write((short)16);    // bits per sample
            writer.Write("data"u8);
            writer.Write(pcmAudio.Length);
            writer.Write(pcmAudio);
        }
        wavStream.Position = 0;

        var sb = new System.Text.StringBuilder();
        await foreach (var segment in _processor.ProcessAsync(wavStream, ct))
        {
            sb.Append(segment.Text);
        }

        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        _processor?.Dispose();
    }
}
