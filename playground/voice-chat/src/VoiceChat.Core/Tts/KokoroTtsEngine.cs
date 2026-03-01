using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;

namespace VoiceChat.Core.Tts;

/// <summary>
/// KokoroSharp-based text-to-speech engine (Kokoro-82M).
/// Uses KokoroWavSynthesizer for simple WAV byte output.
/// </summary>
public sealed class KokoroTtsEngine : IDisposable
{
    private KokoroTTS? _tts;

    public event Action<string>? StatusChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        StatusChanged?.Invoke("Downloading/loading Kokoro TTS model...");

        _tts = await KokoroTTS.LoadModelAsync(
            KModel.float32,
            progress => StatusChanged?.Invoke($"Loading TTS model: {progress:P0}"),
            null);

        StatusChanged?.Invoke("Kokoro TTS ready.");
    }

    /// <summary>
    /// Synthesizes text and plays through built-in KokoroSharp audio.
    /// Returns when synthesis is complete (playback may continue).
    /// </summary>
    public async Task SpeakAsync(string text, string voiceName = "af_heart", float speed = 1.0f, CancellationToken ct = default)
    {
        if (_tts is null)
            throw new InvalidOperationException("KokoroTtsEngine not initialized. Call InitializeAsync first.");

        var voice = KokoroVoiceManager.GetVoice(voiceName);
        var config = new KokoroTTSPipelineConfig { Speed = speed };

        var handle = _tts.Speak(text, voice, config);

        // Wait for synthesis job to complete
        while (!handle.Job.isDone && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }

        ct.ThrowIfCancellationRequested();

        // Wait for playback to finish
        while (handle.ReadyPlaybackHandles.Count > 0 &&
               handle.ReadyPlaybackHandles.Any(h => h.State is KokoroPlaybackHandleState.Queued or KokoroPlaybackHandleState.InProgress) &&
               !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }

        if (ct.IsCancellationRequested)
        {
            _tts.StopPlayback();
            ct.ThrowIfCancellationRequested();
        }
    }

    public string[] GetAvailableVoices()
    {
        return
        [
            "af_heart",
            "af_bella",
            "af_sarah",
            "am_adam",
            "am_michael",
            "bf_emma",
            "bm_george",
        ];
    }

    public void Dispose()
    {
        _tts?.Dispose();
    }
}
