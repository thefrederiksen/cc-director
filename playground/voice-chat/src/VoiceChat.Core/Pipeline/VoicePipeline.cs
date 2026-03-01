using System.Diagnostics;
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
/// </summary>
public sealed class VoicePipeline : IDisposable
{
    private readonly AudioCapture _capture;
    private readonly WhisperSttEngine _stt;
    private readonly KokoroTtsEngine _tts;
    private readonly ClaudeCodeBridge _llm;

    public AudioCapture Capture => _capture;

    public event Action<string>? StatusChanged;
    public event Action<ChatMessage>? UserMessageReady;
    public event Action<ChatMessage>? AssistantMessageReady;

    public string SelectedVoice { get; set; } = "af_heart";
    public float SpeechSpeed { get; set; } = 1.0f;

    public VoicePipeline(string claudePath = "claude", string? workingDirectory = null)
    {
        VoiceLog.Write("[VoicePipeline] Creating pipeline.");

        _capture = new AudioCapture();
        _stt = new WhisperSttEngine();
        _tts = new KokoroTtsEngine();
        _llm = new ClaudeCodeBridge(claudePath, workingDirectory);

        _capture.StatusChanged += s => { VoiceLog.Write($"[AudioCapture] {s}"); StatusChanged?.Invoke(s); };
        _stt.StatusChanged += s => { VoiceLog.Write($"[WhisperSTT] {s}"); StatusChanged?.Invoke(s); };
        _tts.StatusChanged += s => { VoiceLog.Write($"[KokoroTTS] {s}"); StatusChanged?.Invoke(s); };
        _llm.StatusChanged += s => { VoiceLog.Write($"[ClaudeCode] {s}"); StatusChanged?.Invoke(s); };
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        VoiceLog.Write("[VoicePipeline] InitializeAsync: starting.");
        StatusChanged?.Invoke("Initializing pipeline...");

        VoiceLog.Write("[VoicePipeline] InitializeAsync: loading STT...");
        await _stt.InitializeAsync(ct: ct);
        VoiceLog.Write("[VoicePipeline] InitializeAsync: STT ready.");

        VoiceLog.Write("[VoicePipeline] InitializeAsync: loading TTS...");
        await _tts.InitializeAsync(ct);
        VoiceLog.Write("[VoicePipeline] InitializeAsync: TTS ready.");

        VoiceLog.Write("[VoicePipeline] InitializeAsync: pipeline ready.");
        StatusChanged?.Invoke("Pipeline ready. Hold the button to talk.");
    }

    public void StartRecording()
    {
        VoiceLog.Write("[VoicePipeline] StartRecording.");
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

        if (audioData.Length == 0)
        {
            StatusChanged?.Invoke("No audio captured.");
            return;
        }

        var latency = new LatencyInfo();
        var sw = Stopwatch.StartNew();

        // -- STT --
        StatusChanged?.Invoke("Transcribing...");
        VoiceLog.Write("[VoicePipeline] STT: transcribing...");
        var transcription = await _stt.TranscribeAsync(audioData, ct);
        latency.SttMs = (int)sw.ElapsedMilliseconds;
        VoiceLog.Write($"[VoicePipeline] STT: completed in {latency.SttMs}ms -> \"{transcription}\"");
        sw.Restart();

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
        sw.Restart();

        // -- TTS + Playback (KokoroSharp handles both) --
        StatusChanged?.Invoke("Speaking...");
        VoiceLog.Write("[VoicePipeline] TTS: speaking...");
        await _tts.SpeakAsync(response, SelectedVoice, SpeechSpeed, ct);
        latency.TtsMs = (int)sw.ElapsedMilliseconds;
        VoiceLog.Write($"[VoicePipeline] TTS: completed in {latency.TtsMs}ms");

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

    public void Dispose()
    {
        VoiceLog.Write("[VoicePipeline] Disposing.");
        _capture.Dispose();
        _stt.Dispose();
        _tts.Dispose();
    }
}
