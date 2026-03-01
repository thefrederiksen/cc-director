using Whisper.net;
using Whisper.net.Ggml;

namespace VoiceChat.Core.Stt;

/// <summary>
/// Whisper.net-based speech-to-text engine.
/// Downloads and caches the GGML model, then transcribes audio buffers.
/// </summary>
public sealed class WhisperSttEngine : IDisposable
{
    private WhisperProcessor? _processor;
    private readonly string _modelsDir;

    public event Action<string>? StatusChanged;

    public WhisperSttEngine()
    {
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "voice-chat", "models", "whisper");
        Directory.CreateDirectory(_modelsDir);
    }

    public async Task InitializeAsync(GgmlType modelSize = GgmlType.Small, CancellationToken ct = default)
    {
        var modelFileName = $"ggml-{modelSize.ToString().ToLowerInvariant()}.bin";
        var modelPath = Path.Combine(_modelsDir, modelFileName);

        if (!File.Exists(modelPath))
        {
            StatusChanged?.Invoke($"Downloading Whisper {modelSize} model...");
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(modelSize, cancellationToken: ct);
            using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream, ct);
            StatusChanged?.Invoke($"Whisper {modelSize} model downloaded.");
        }

        StatusChanged?.Invoke("Loading Whisper model...");
        var factory = WhisperFactory.FromPath(modelPath);
        _processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        StatusChanged?.Invoke("Whisper ready.");
    }

    /// <summary>
    /// Transcribes 16-bit PCM audio at 16kHz mono.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcmAudio, CancellationToken ct = default)
    {
        if (_processor is null)
            throw new InvalidOperationException("WhisperSttEngine not initialized. Call InitializeAsync first.");

        // Convert 16-bit PCM to float samples for Whisper
        var sampleCount = pcmAudio.Length / 2;
        var floatSamples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample16 = BitConverter.ToInt16(pcmAudio, i * 2);
            floatSamples[i] = sample16 / (float)short.MaxValue;
        }

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

        var text = string.Empty;
        await foreach (var segment in _processor.ProcessAsync(wavStream, ct))
        {
            text += segment.Text;
        }

        return text.Trim();
    }

    public void Dispose()
    {
        _processor?.Dispose();
    }
}
