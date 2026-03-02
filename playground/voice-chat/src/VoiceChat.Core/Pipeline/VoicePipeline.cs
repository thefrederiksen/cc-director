using System.Diagnostics;
using Whisper.net.Ggml;
using VoiceChat.Core.Llm;
using VoiceChat.Core.Logging;
using VoiceChat.Core.Models;
using VoiceChat.Core.Pipeline;
using VoiceChat.Core.Recording;
using VoiceChat.Core.Stt;
using VoiceChat.Core.Tts;

namespace VoiceChat.Core.Pipeline;

/// <summary>
/// Orchestrates the full voice pipeline: STT -> LLM -> TTS + playback.
/// Measures latency at each stage. Logs all operations. Saves recordings to disk.
/// Supports multiple STT engines including streaming engines with partial results.
/// </summary>
public sealed class VoicePipeline : IDisposable
{
    private readonly AudioCapture _capture;
    private readonly SttEngineRegistry _sttRegistry;
    private ISttEngine _currentStt;
    private readonly KokoroTtsEngine _tts;
    private readonly ClaudeCodeBridge _llm;
    private readonly CustomDictionary _dictionary;

    public AudioCapture Capture => _capture;
    public SttEngineRegistry SttRegistry => _sttRegistry;
    public CustomDictionary Dictionary => _dictionary;

    public event Action<string>? StatusChanged;
    public event Action<ChatMessage>? UserMessageReady;
    public event Action<ChatMessage>? AssistantMessageReady;
    public event Action<string>? PartialTranscriptionChanged;

    public string SelectedVoice { get; set; } = "af_heart";
    public float SpeechSpeed { get; set; } = 1.0f;

    public VoicePipeline(string claudePath = "claude", string? workingDirectory = null)
    {
        VoiceLog.Write("[VoicePipeline] Creating pipeline.");

        _capture = new AudioCapture();
        _tts = new KokoroTtsEngine();
        _llm = new ClaudeCodeBridge(claudePath, workingDirectory);
        _dictionary = new CustomDictionary();

        // Register all STT engines
        _sttRegistry = new SttEngineRegistry();
        _sttRegistry.RegisterEngine(new WhisperSttEngine(GgmlType.Tiny));
        _sttRegistry.RegisterEngine(new WhisperSttEngine(GgmlType.Base));
        _sttRegistry.RegisterEngine(new WhisperSttEngine(GgmlType.Small));
        _sttRegistry.RegisterEngine(new VoskSttEngine());

        // Default to Vosk (fast streaming STT with no GPU required)
        _currentStt = _sttRegistry.GetEngine("Vosk Small EN")
            ?? throw new InvalidOperationException("Default STT engine 'Vosk Small EN' not found in registry.");

        // Push dictionary words to Vosk as phrase hints
        ApplyPhraseHintsToCurrentEngine();
        _dictionary.WordsChanged += ApplyPhraseHintsToCurrentEngine;

        _capture.StatusChanged += s => { VoiceLog.Write($"[AudioCapture] {s}"); StatusChanged?.Invoke(s); };
        _tts.StatusChanged += s => { VoiceLog.Write($"[KokoroTTS] {s}"); StatusChanged?.Invoke(s); };
        _llm.StatusChanged += s => { VoiceLog.Write($"[ClaudeCode] {s}"); StatusChanged?.Invoke(s); };
    }

    public string[] GetSttEngines()
    {
        return _sttRegistry.GetEngines().Select(e => e.DisplayName).ToArray();
    }

    public string GetCurrentSttEngine() => _currentStt.DisplayName;

    public async Task SetSttEngineAsync(string displayName, CancellationToken ct = default)
    {
        VoiceLog.Write($"[VoicePipeline] SetSttEngineAsync: switching to {displayName}");

        var engine = _sttRegistry.GetEngine(displayName);
        if (engine is null)
            throw new InvalidOperationException($"STT engine '{displayName}' not found in registry.");

        if (!engine.IsReady)
        {
            StatusChanged?.Invoke($"Initializing {displayName}...");
            WireSttStatusEvents(engine);
            await engine.InitializeAsync(ct);
        }

        // Unwire old streaming events
        UnwireStreamingEvents(_currentStt);

        _currentStt = engine;
        ApplyPhraseHintsToCurrentEngine();
        VoiceLog.Write($"[VoicePipeline] SetSttEngineAsync: now using {displayName}");
        StatusChanged?.Invoke($"STT engine: {displayName}");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        VoiceLog.Write("[VoicePipeline] InitializeAsync: starting.");
        StatusChanged?.Invoke("Initializing pipeline...");

        VoiceLog.Write("[VoicePipeline] InitializeAsync: loading STT...");
        WireSttStatusEvents(_currentStt);
        await _currentStt.InitializeAsync(ct);
        VoiceLog.Write("[VoicePipeline] InitializeAsync: STT ready.");

        // TTS disabled for faster testing - skip initialization
        VoiceLog.Write("[VoicePipeline] InitializeAsync: TTS skipped (disabled).");

        VoiceLog.Write("[VoicePipeline] InitializeAsync: pipeline ready.");
        StatusChanged?.Invoke("Pipeline ready. Hold the button to talk.");
    }

    public void StartRecording()
    {
        VoiceLog.Write("[VoicePipeline] StartRecording.");

        // If current engine supports streaming, begin a stream and wire audio chunks
        if (_currentStt is IStreamingSttEngine streaming)
        {
            VoiceLog.Write("[VoicePipeline] StartRecording: streaming engine detected, beginning stream.");
            streaming.BeginStream();
            _capture.AudioChunkAvailable += OnAudioChunkForStreaming;

            // Wire partial results
            streaming.PartialResultReady += OnPartialResult;
        }

        _capture.StartRecording();
    }

    /// <summary>
    /// Stops recording and processes the full pipeline: STT -> LLM -> TTS + playback.
    /// Saves all recorded audio to the audio library.
    /// </summary>
    public async Task StopRecordingAndProcessAsync(CancellationToken ct = default)
    {
        var audioData = _capture.StopRecording();
        VoiceLog.Write($"[VoicePipeline] StopRecording: {audioData.Length} bytes captured.");

        // Unwire streaming events
        if (_currentStt is IStreamingSttEngine streaming)
        {
            _capture.AudioChunkAvailable -= OnAudioChunkForStreaming;
            streaming.PartialResultReady -= OnPartialResult;
        }

        if (audioData.Length == 0)
        {
            StatusChanged?.Invoke("No audio captured.");
            return;
        }

        var latency = new LatencyInfo();
        var sw = Stopwatch.StartNew();

        // -- STT --
        string transcription;
        if (_currentStt is IStreamingSttEngine streamingStt)
        {
            StatusChanged?.Invoke("Finalizing transcription...");
            VoiceLog.Write("[VoicePipeline] STT: ending stream for final result...");
            transcription = streamingStt.EndStream();
        }
        else
        {
            StatusChanged?.Invoke("Transcribing...");
            VoiceLog.Write("[VoicePipeline] STT: transcribing (batch)...");
            transcription = await _currentStt.TranscribeAsync(audioData, ct);
        }

        // Apply custom dictionary correction
        transcription = _dictionary.CorrectTranscription(transcription);

        latency.SttMs = (int)sw.ElapsedMilliseconds;
        VoiceLog.Write($"[VoicePipeline] STT: completed in {latency.SttMs}ms -> \"{transcription}\"");
        sw.Restart();

        // Clear partial transcription
        PartialTranscriptionChanged?.Invoke(string.Empty);

        // Save recording to audio library (with transcription if available)
        AudioLibrary.Save(audioData, sampleRate: 16000, bitsPerSample: 16, channels: 1, transcription);

        if (string.IsNullOrWhiteSpace(transcription))
        {
            StatusChanged?.Invoke("No speech detected.");
            VoiceLog.Write("[VoicePipeline] STT: no speech detected.");
            return;
        }

        UserMessageReady?.Invoke(new ChatMessage
        {
            Role = "user",
            Text = transcription,
        });

        // -- LLM --
        StatusChanged?.Invoke("Thinking...");
        VoiceLog.Write($"[VoicePipeline] LLM: sending prompt...");
        var response = await _llm.SendPromptAsync(transcription, ct);
        latency.LlmMs = (int)sw.ElapsedMilliseconds;
        VoiceLog.Write($"[VoicePipeline] LLM: completed in {latency.LlmMs}ms -> \"{response[..Math.Min(100, response.Length)]}...\"");

        VoiceLog.Write($"[VoicePipeline] Pipeline complete: {latency}");

        AssistantMessageReady?.Invoke(new ChatMessage
        {
            Role = "assistant",
            Text = response,
            Latency = latency,
        });

        StatusChanged?.Invoke("Ready.");
    }

    public string[] GetAvailableVoices() => _tts.GetAvailableVoices();

    public void ResetConversation()
    {
        _llm.ResetSession();
        VoiceLog.Write("[VoicePipeline] Conversation reset.");
        StatusChanged?.Invoke("Conversation reset.");
    }

    private void ApplyPhraseHintsToCurrentEngine()
    {
        if (_currentStt is VoskSttEngine vosk)
        {
            var words = _dictionary.GetWords();
            vosk.SetPhraseHints(words);
        }
    }

    private void WireSttStatusEvents(ISttEngine engine)
    {
        engine.StatusChanged += s => { VoiceLog.Write($"[STT:{engine.DisplayName}] {s}"); StatusChanged?.Invoke(s); };
    }

    private void UnwireStreamingEvents(ISttEngine engine)
    {
        if (engine is IStreamingSttEngine streaming)
        {
            _capture.AudioChunkAvailable -= OnAudioChunkForStreaming;
            streaming.PartialResultReady -= OnPartialResult;
        }
    }

    private void OnAudioChunkForStreaming(byte[] buffer, int bytesRecorded)
    {
        if (_currentStt is IStreamingSttEngine streaming)
        {
            streaming.FeedAudioChunk(buffer, bytesRecorded);
        }
    }

    private void OnPartialResult(string partialText)
    {
        var corrected = _dictionary.CorrectTranscription(partialText);
        PartialTranscriptionChanged?.Invoke(corrected);
    }

    public void Dispose()
    {
        VoiceLog.Write("[VoicePipeline] Disposing.");
        _capture.Dispose();

        foreach (var engine in _sttRegistry.GetEngines())
        {
            engine.Dispose();
        }

        _tts.Dispose();
    }
}
