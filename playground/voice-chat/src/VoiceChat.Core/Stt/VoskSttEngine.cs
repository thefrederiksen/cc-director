using System.IO.Compression;
using System.Text.Json;
using VoiceChat.Core.Logging;

namespace VoiceChat.Core.Stt;

/// <summary>
/// Vosk-based streaming speech-to-text engine.
/// Auto-downloads vosk-model-small-en-us-0.15 (~40 MB).
/// Provides real-time partial results while audio is being recorded.
/// </summary>
public sealed class VoskSttEngine : IStreamingSttEngine
{
    private const string ModelName = "vosk-model-small-en-us-0.15";
    private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const float SampleRate = 16000f;

    private readonly string _modelsDir;
    private Vosk.Model? _model;
    private Vosk.VoskRecognizer? _recognizer;
    private readonly List<string> _completedUtterances = new();
    private string[]? _phraseHints;

    public string DisplayName => "Vosk Small EN";
    public string Description => "~40 MB | Real-time streaming";
    public bool IsReady => _model is not null;

    public event Action<string>? StatusChanged;
    public event Action<string>? PartialResultReady;

    public VoskSttEngine()
    {
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "voice-chat", "models", "vosk");
        Directory.CreateDirectory(_modelsDir);
    }

    /// <summary>
    /// Sets phrase hints that bias recognition toward these words.
    /// Uses Vosk grammar mode with [unk] for non-hint words.
    /// </summary>
    public void SetPhraseHints(string[] words)
    {
        _phraseHints = words.Length > 0 ? words : null;
        var status = _phraseHints is not null
            ? $"{_phraseHints.Length} phrase hints set"
            : "Phrase hints cleared";
        VoiceLog.Write($"[VoskSttEngine] SetPhraseHints: {status}");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        VoiceLog.Write("[VoskSttEngine] InitializeAsync: starting.");

        var modelDir = Path.Combine(_modelsDir, ModelName);

        if (!Directory.Exists(modelDir))
        {
            await DownloadAndExtractModelAsync(modelDir, ct);
        }

        StatusChanged?.Invoke("Loading Vosk model...");
        Vosk.Vosk.SetLogLevel(-1); // Suppress native logging
        _model = new Vosk.Model(modelDir);

        VoiceLog.Write("[VoskSttEngine] InitializeAsync: Vosk ready.");
        StatusChanged?.Invoke("Vosk ready.");
    }

    public void BeginStream()
    {
        if (_model is null)
            throw new InvalidOperationException("VoskSttEngine not initialized. Call InitializeAsync first.");

        VoiceLog.Write("[VoskSttEngine] BeginStream.");
        _recognizer?.Dispose();
        _completedUtterances.Clear();

        // Use standard recognition (no grammar restriction) for full sentence capture.
        // Vosk grammar mode is too restrictive -- it drops all non-dictionary words.
        _recognizer = new Vosk.VoskRecognizer(_model, SampleRate);
    }

    public void FeedAudioChunk(byte[] buffer, int bytesRecorded)
    {
        if (_recognizer is null) return;

        if (_recognizer.AcceptWaveform(buffer, bytesRecorded))
        {
            // Complete utterance detected - get and accumulate result
            var json = _recognizer.Result();
            var text = ExtractText(json);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _completedUtterances.Add(text);
                VoiceLog.Write($"[VoskSttEngine] Utterance completed: \"{text}\"");
                // Show accumulated text so far as the partial
                PartialResultReady?.Invoke(string.Join(" ", _completedUtterances));
            }
        }
        else
        {
            // Still processing - get partial
            var json = _recognizer.PartialResult();
            var partial = ExtractPartial(json);
            if (!string.IsNullOrWhiteSpace(partial))
            {
                // Show accumulated + current partial
                var accumulated = _completedUtterances.Count > 0
                    ? string.Join(" ", _completedUtterances) + " " + partial
                    : partial;
                PartialResultReady?.Invoke(accumulated);
            }
        }
    }

    public string EndStream()
    {
        if (_recognizer is null) return string.Empty;

        VoiceLog.Write("[VoskSttEngine] EndStream.");
        var json = _recognizer.FinalResult();
        var finalText = ExtractText(json);

        // Combine accumulated utterances with any remaining final text
        if (!string.IsNullOrWhiteSpace(finalText))
            _completedUtterances.Add(finalText);

        var result = string.Join(" ", _completedUtterances).Trim();
        VoiceLog.Write($"[VoskSttEngine] EndStream: {_completedUtterances.Count} utterances -> \"{result}\"");

        _completedUtterances.Clear();
        _recognizer.Dispose();
        _recognizer = null;

        return result;
    }

    /// <summary>
    /// Batch fallback: feeds all audio at once and returns the final result.
    /// </summary>
    public Task<string> TranscribeAsync(byte[] pcmAudio, CancellationToken ct = default)
    {
        if (_model is null)
            throw new InvalidOperationException("VoskSttEngine not initialized. Call InitializeAsync first.");

        using var recognizer = new Vosk.VoskRecognizer(_model, SampleRate);

        // Feed in chunks to avoid large single buffer issues
        const int chunkSize = 4096;
        for (var offset = 0; offset < pcmAudio.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = Math.Min(chunkSize, pcmAudio.Length - offset);
            var chunk = new byte[remaining];
            Array.Copy(pcmAudio, offset, chunk, 0, remaining);
            recognizer.AcceptWaveform(chunk, remaining);
        }

        var json = recognizer.FinalResult();
        var text = ExtractText(json);
        return Task.FromResult(text);
    }

    private async Task DownloadAndExtractModelAsync(string modelDir, CancellationToken ct)
    {
        var zipPath = Path.Combine(_modelsDir, $"{ModelName}.zip");

        StatusChanged?.Invoke($"Downloading Vosk model ({ModelName})...");
        VoiceLog.Write($"[VoskSttEngine] Downloading model from {ModelUrl}");

        using (var http = new HttpClient())
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = File.Create(zipPath);
            await stream.CopyToAsync(fileStream, ct);
        }

        StatusChanged?.Invoke("Extracting Vosk model...");
        VoiceLog.Write("[VoskSttEngine] Extracting model zip.");
        ZipFile.ExtractToDirectory(zipPath, _modelsDir, overwriteFiles: true);

        // Clean up zip
        File.Delete(zipPath);

        if (!Directory.Exists(modelDir))
            throw new InvalidOperationException($"Model extraction failed: directory {modelDir} not found after extraction.");

        VoiceLog.Write("[VoskSttEngine] Model downloaded and extracted.");
        StatusChanged?.Invoke("Vosk model ready.");
    }

    private static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
            return textProp.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static string ExtractPartial(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("partial", out var partialProp))
            return partialProp.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _model?.Dispose();
    }
}
