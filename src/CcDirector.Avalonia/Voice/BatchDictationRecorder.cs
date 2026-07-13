using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Whole-audio dictation recorder for the desktop Speak dialog (issue #589).
///
/// This is the migrated, BATCH-ONLY dictation path. It captures EVERY byte of
/// microphone audio locally for the whole turn and sends it to the Gateway transcription owner exactly
/// once, after the user stops. There is deliberately
/// NO realtime/streaming transcription and NO live partial preview: the realtime
/// model lightly rewords phrasing and the live-partials experience is exactly the
/// partial transcription the product removed, so desktop dictation now matches the
/// agreed whole-audio-batch flow.
///
/// Lifecycle (driven by <see cref="SpeakDialog"/>):
///
///   var rec = new BatchDictationRecorder(options);
///   rec.OnAudioBands     += bands => /* drive equalizer */ ;
///   rec.OnInputRms       += rms   => /* low-level hint */ ;
///   rec.OnCaptureStarted += ()    => /* driver asked to start (setup done) */ ;
///   rec.OnCaptureLive    += ()    => /* first real audio in: flip to RECORDING + ready cue */ ;
///   await rec.StartAsync();   // mic opens, audio buffers locally
///   // user talks (no text appears - no live preview)
///   var result = await rec.TranscribeAsync();  // ONE batch transcription call
///   // result.CleanedTranscript is what we hand back to the prompt input
///
/// The completeness gate (issue #586) is enforced here as "whole audio in": an
/// empty capture (an interrupted turn that produced no audio) fails loud with
/// <see cref="NoAudioCapturedException"/> rather than transcribing partial input,
/// and the shared pipeline itself refuses an empty blob. The only
/// post-transcription transform is the validated dictionary corrector, so a turn
/// with no dictionary term comes back byte-identical to the raw transcription.
///
/// No browser, no WebSocket, no localhost hop, no realtime socket.
/// </summary>
public sealed class BatchDictationRecorder : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly DictionaryResolver _dictionaryResolver;
    private readonly int _micDeviceNumber;

    // Builds the audio source for a device number. Production builds a NAudio
    // MicAudioCapture; tests inject a fake to drive the capture/stop sequencing
    // without a real microphone (the IAudioSource seam).
    private readonly Func<int, IAudioSource> _audioSourceFactory;

    // Test seam: replaces the post-snapshot transcription (WAV wrap, Gateway
    // transcription call, audit log) with a stub that receives the
    // snapshotted PCM. Null in production, where the real pipeline runs. Lets a test
    // assert exactly which captured bytes reach transcription - i.e. that the tail is
    // not clipped - without any network.
    private readonly Func<byte[], string, CancellationToken, Task<DictationResult>>? _transcribeOverride;

    private IAudioSource? _mic;

    // The whole-turn PCM16 accumulator. Every captured chunk is appended here in
    // capture order; nothing leaves the machine until TranscribeAsync wraps the
    // whole buffer in one WAV blob and sends it to the Gateway transcription endpoint.
    private readonly MemoryStream _audio = new();
    private readonly object _audioLock = new();

    private bool _started;
    private bool _stopped;
    private bool _disposed;

    // Session-record state so the desktop path leaves the same JSONL audit trail
    // as the other dictation surfaces (issue #190).
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string _profile = "default";
    private DateTime _sessionStartUtc;
    private System.Diagnostics.Stopwatch? _recordingStopwatch;

    /// <summary>Fires for every captured chunk with a per-band (0..1) spectrum for the UI equalizer.</summary>
    public event Action<double[]>? OnAudioBands;

    /// <summary>Fires for every captured chunk with the raw int16 RMS amplitude, driving the "speak up" hint.</summary>
    public event Action<double>? OnInputRms;

    /// <summary>
    /// Fires the instant the microphone is asked to start capturing (right after
    /// <c>StartRecording</c> returns). This is the SETUP moment, before any audio has
    /// actually been delivered by the driver. There is no separate "connected" event
    /// because there is no network connect before capture - transcription happens once,
    /// after the user stops.
    /// </summary>
    public event Action? OnCaptureStarted;

    /// <summary>
    /// Fires exactly ONCE, when the first buffer of real audio actually lands in the
    /// capture buffer - the honest "the microphone is now hearing your voice" moment,
    /// as opposed to <see cref="OnCaptureStarted"/> which only means the driver was
    /// asked to start. The desktop dialog holds its "GETTING READY" state until this
    /// fires, then flips to RECORDING and plays the ready cue together, so neither the
    /// red state nor the sound ever claims the mic is live before audio is flowing.
    /// May fire on NAudio's worker thread, so a UI handler must marshal to the UI thread.
    /// </summary>
    public event Action? OnCaptureLive;

    // One-shot latch for OnCaptureLive: set the first time a non-empty chunk is
    // appended, so the "mic is live" signal is raised once per recorder, not per chunk.
    private bool _captureLiveRaised;

    /// <summary>
    /// Fires as transcription progresses with (completedParts, totalParts). A long recording is split
    /// into several bounded transcription requests; this lets the dialog show "transcribing part N of
    /// M" instead of a silent wait. A short clip reports a single part. May fire on a background
    /// thread, so a UI handler must marshal to the UI thread.
    /// </summary>
    public event Action<int, int>? OnTranscriptionProgress;

    /// <param name="micDeviceNumber">
    /// WaveIn device number to capture from. Defaults to
    /// <see cref="MicDevices.DefaultDeviceNumber"/> (the Windows default mic).
    /// </param>
    public BatchDictationRecorder(AgentOptions options, int micDeviceNumber = MicDevices.DefaultDeviceNumber)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Resolve the dictation dictionary the same way: the Gateway's shared glossary when
        // attached, the local cache when standalone (#253). A Cockpit edit reaches this Director.
        _dictionaryResolver = new DictionaryResolver(options);
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = static device => new MicAudioCapture(device);
        _transcribeOverride = null;
    }

    /// <summary>
    /// Test-only constructor (the IAudioSource seam). Injects the audio source so the
    /// capture-and-stop sequencing can be driven by a fake, and the transcription so
    /// the snapshotted PCM can be inspected, both without a real mic or the network.
    /// </summary>
    internal BatchDictationRecorder(
        AgentOptions options,
        Func<int, IAudioSource> audioSourceFactory,
        Func<byte[], string, CancellationToken, Task<DictationResult>> transcribeOverride,
        int micDeviceNumber = MicDevices.DefaultDeviceNumber)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dictionaryResolver = new DictionaryResolver(options);
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = audioSourceFactory ?? throw new ArgumentNullException(nameof(audioSourceFactory));
        _transcribeOverride = transcribeOverride ?? throw new ArgumentNullException(nameof(transcribeOverride));
    }

    /// <summary>
    /// Open the microphone and start buffering audio locally. Returns once capture
    /// is live. No transcription happens here - the whole clip is transcribed once
    /// on <see cref="TranscribeAsync"/>.
    /// </summary>
    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
        if (_started) throw new InvalidOperationException("BatchDictationRecorder already started");
        FileLog.Write($"[BatchDictationRecorder] StartAsync: profile={profile}, device={_micDeviceNumber}");

        // CAPTURE FIRST - open the mic and start buffering every byte locally. There
        // is no network work before capture: the method, key, and dictionary are
        // resolved later, at TranscribeAsync, for the single batch transcription. So
        // the bars move and audio is captured from the very first frame and the
        // dialog can flip to RECORDING the instant capture is live.
        _mic = _audioSourceFactory(_micDeviceNumber);
        _mic.OnAudioChunk += AppendChunk;
        // Equalizer + level hint are optional UI cosmetics: wire them only when the
        // source actually emits them (the real mic does; a headless test source need not).
        if (_mic is IAudioMeterSource meter)
        {
            meter.OnAudioBands += RaiseAudioBands;
            meter.OnInputRms += RaiseInputRms;
        }

        try
        {
            _mic.Start();
        }
        catch
        {
            // A failed start must never orphan the microphone. DisposeAsync is
            // idempotent and null-safe for this half-built state.
            await DisposeAsync();
            throw;
        }

        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        _sessionStartUtc = DateTime.UtcNow;
        _recordingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _started = true;

        // Capture is confirmed live. Let the UI anchor its timer and flip to RECORDING.
        OnCaptureStarted?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop the microphone, drain NAudio's final buffered audio, and return the whole captured clip as
    /// a WAV blob WITHOUT transcribing it. This is the durable-dictation split (issue #1130): the
    /// fire-and-forget Send saves these bytes to disk (<see cref="CcDirector.Core.Dictation.DictationRecordingStore"/>)
    /// before its single transcription attempt and keeps the file when transcription fails - so a
    /// failed or slow transcription can never lose the recording. Enforces the same
    /// completeness gate as <see cref="TranscribeAsync"/> (an empty capture throws
    /// <see cref="NoAudioCapturedException"/>), so an interrupted turn with no audio is never persisted.
    /// The recorder is consumed (stopped) here; call this OR <see cref="TranscribeAsync"/>, once.
    /// </summary>
    public async Task<CapturedAudio> StopAndGetWavAsync()
    {
        FileLog.Write("[BatchDictationRecorder] StopAndGetWavAsync");
        var (pcm, _, _) = await StopAndSnapshotAsync();
        var wav = WavWriter.WrapPcm16(
            pcm, MicAudioCapture.SampleRate, MicAudioCapture.Channels, MicAudioCapture.BitsPerSample);
        return new CapturedAudio(wav, _recordingStopwatch?.ElapsedMilliseconds ?? 0);
    }

    /// <summary>
    /// Stop the mic, WAIT for NAudio to flush its final buffered audio, then snapshot the whole-turn PCM.
    /// Shared by <see cref="TranscribeAsync"/> and <see cref="StopAndGetWavAsync"/> so both paths capture
    /// the identical bytes. WaveInEvent keeps capturing for up to one buffer after the stop and delivers
    /// the trailing words via AppendChunk on its worker thread, then raises RecordingStopped; StopAsync
    /// completes on that event, so the whole tail of speech is appended before the snapshot. Detaching
    /// the handler BEFORE the drain (the old order) discarded that tail and clipped the end of speech.
    /// Enforces the completeness gate (issue #586): an empty capture throws <see cref="NoAudioCapturedException"/>.
    /// </summary>
    private async Task<(byte[] Pcm, CaptureHealth? Health, string Device)> StopAndSnapshotAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
        if (!_started) throw new InvalidOperationException("BatchDictationRecorder not started");
        if (_stopped) throw new InvalidOperationException("BatchDictationRecorder already stopped");
        _stopped = true;

        var device = _mic?.Description ?? MicDevices.DescribeDevice(_micDeviceNumber);
        CaptureHealth? captureHealth = null;
        if (_mic is not null)
        {
            await _mic.StopAsync(TimeSpan.FromMilliseconds(750));
            // Read capture-health AFTER the drain (counters are final) and BEFORE detaching,
            // so the audit record can carry the per-recording diagnostics (issue #863).
            captureHealth = (_mic as IAudioCaptureDiagnostics)?.GetCaptureHealth();
            _mic.OnAudioChunk -= AppendChunk;
        }
        _recordingStopwatch?.Stop();

        byte[] pcm;
        lock (_audioLock)
        {
            pcm = _audio.ToArray();
        }

        // Completeness gate: an empty capture can never produce a real transcript.
        // Fail explicitly so an interrupted turn re-records rather than silently
        // returning empty text (issue #586). NoAudioCapturedException names the
        // device the user must check.
        if (pcm.Length == 0)
        {
            FileLog.Write("[BatchDictationRecorder] no audio captured; refusing (completeness gate)");
            throw new NoAudioCapturedException(device);
        }

        return (pcm, captureHealth, device);
    }

    /// <summary>
    /// Stop the microphone, then transcribe the whole captured clip exactly once
    /// through the Gateway transcription endpoint, applying the dictionary corrector
    /// only. Returns the raw transcript, the corrected
    /// transcript, and how many dictionary words were corrected.
    ///
    /// Enforces the completeness gate (issue #586): a turn that captured no audio
    /// fails loud with <see cref="NoAudioCapturedException"/> rather than producing
    /// a partial/empty transcript. Transcription failures throw so the caller
    /// surfaces them - a missing transcript is a real failure, not papered over.
    /// </summary>
    public async Task<DictationResult> TranscribeAsync(CancellationToken ct = default)
    {
        FileLog.Write("[BatchDictationRecorder] TranscribeAsync");
        var (pcm, captureHealth, device) = await StopAndSnapshotAsync();

        // Test seam: hand the snapshotted PCM to the injected stub instead of the real
        // network pipeline. The empty-audio gate above still runs first, so the stub
        // only ever sees a non-empty capture - exactly what the real path transcribes.
        if (_transcribeOverride is not null)
            return await _transcribeOverride(pcm, device, ct);

        // Pull a local glossary snapshot for diagnostics. The Gateway applies the authoritative
        // glossary correction during /transcription.
        var dictionary = await _dictionaryResolver.ResolveAsync(ct);

        // Wrap the whole captured PCM in one WAV blob and transcribe ONCE through the
        // Gateway transcription endpoint. The dictionary corrector is the only text transform.
        var wav = WavWriter.WrapPcm16(
            pcm, MicAudioCapture.SampleRate, MicAudioCapture.Channels, MicAudioCapture.BitsPerSample);

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        var gateway = await new GatewayTranscriptionClient().TranscribeAsync(
            wav, "dictation.wav", "audio/wav", applyCorrection: true, ct);
        stopWatch.Stop();

        FileLog.Write($"[BatchDictationRecorder] transcribed via Gateway: len={gateway.Text.Length}, "
            + $"mode={gateway.Mode}, model={gateway.Model}");

        // Capture-health line (issue #863): a byte deficit paired with large callback GAPS
        // (and small handler self-time) points upstream - the audio was under-delivered
        // before we saw it (e.g. Remote Desktop audio redirection); a deficit paired with
        // large handler self-time points at a local capture-thread stall. This is what tells
        // the two apart so any future fix is aimed at the real cause.
        if (captureHealth is { } ch)
            FileLog.Write($"[BatchDictationRecorder] capture-health: capturedBytes={ch.CapturedBytes}, "
                + $"expectedBytes={ch.ExpectedBytes}, deficit={ch.DeficitFraction:P1}, callbacks={ch.CallbackCount}, "
                + $"maxGapMs={ch.MaxCallbackGapMs:F0}, longGaps={ch.LongGapCount}, maxHandlerMs={ch.MaxHandlerMs:F1}, "
                + $"buffers={ch.NumberOfBuffers}x{ch.BufferMilliseconds}ms");

        // Same JSONL audit record the other dictation surfaces write so a desktop
        // dictation incident keeps its raw text for forensics. Fire-and-forget off
        // the UI-facing path; failures are logged inside TryAppend.
        var record = new DictationSessionRecord(
            TimestampUtc: _sessionStartUtc.ToString("o"),
            SessionId: _sessionId,
            Profile: _profile,
            VocabularyTermCount: dictionary.Vocabulary.Count,
            MistranscriptionPatternCount: dictionary.CommonMistranscriptions.Count,
            RecordingDurationMs: _recordingStopwatch?.ElapsedMilliseconds ?? 0,
            StopToTranscribedMs: stopWatch.ElapsedMilliseconds,
            StopToCleanedMs: stopWatch.ElapsedMilliseconds,
            AudioBytesReceived: pcm.Length,
            RawTranscript: gateway.Text,
            CleanedTranscript: gateway.Text,
            CleanupApplied: false,
            CleanupReason: "gateway-owned transcription",
            CleanupModel: _options.DictationCleanupModel,
            RemoteIp: null,
            ClientError: null,
            Source: "desktop-speak",
            ExpectedAudioBytes: captureHealth?.ExpectedBytes ?? 0,
            CaptureCallbackCount: captureHealth?.CallbackCount ?? 0,
            MaxCaptureCallbackGapMs: captureHealth?.MaxCallbackGapMs ?? 0,
            LongCaptureGapCount: captureHealth?.LongGapCount ?? 0,
            MaxCaptureHandlerMs: captureHealth?.MaxHandlerMs ?? 0,
            CaptureBufferCount: captureHealth?.NumberOfBuffers ?? 0,
            CaptureBufferMs: captureHealth?.BufferMilliseconds ?? 0);
        _ = Task.Run(() => DictationSessionLog.TryAppend(record));

        return new DictationResult(
            RawTranscript: gateway.Text,
            CleanedTranscript: gateway.Text,
            DictionaryWordsCorrected: 0);
    }

    /// <summary>Append one captured PCM16 chunk to the whole-turn buffer. Runs on NAudio's thread.</summary>
    private void AppendChunk(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        lock (_audioLock)
        {
            _audio.Write(chunk, 0, chunk.Length);
        }

        // The first non-empty chunk is the first real audio the driver delivered:
        // the honest "microphone is now capturing your voice" moment. Raise it once,
        // OUTSIDE the audio lock so a UI handler can never stall capture. AppendChunk
        // runs on NAudio's single capture thread, so the latch needs no synchronization.
        if (!_captureLiveRaised)
        {
            _captureLiveRaised = true;
            OnCaptureLive?.Invoke();
        }
    }

    // Named so they can be unsubscribed in DisposeAsync; forward the source's UI-meter
    // events to this recorder's own events for the dialog's equalizer and level hint.
    private void RaiseAudioBands(double[] bands) => OnAudioBands?.Invoke(bands);
    private void RaiseInputRms(double rms) => OnInputRms?.Invoke(rms);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mic is not null)
        {
            _mic.OnAudioChunk -= AppendChunk;
            if (_mic is IAudioMeterSource meter)
            {
                meter.OnAudioBands -= RaiseAudioBands;
                meter.OnInputRms -= RaiseInputRms;
            }
            // Stop discards any undrained tail - fine here: Dispose is the cancel/teardown
            // path. The no-loss drain happens in TranscribeAsync via StopAsync. IAudioSource
            // is not itself IDisposable, so release the concrete resource when it is.
            _mic.Stop();
            if (_mic is IDisposable disposable)
                disposable.Dispose();
        }
        _audio.Dispose();
        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// The result of one whole-audio desktop dictation turn (issue #589): the raw
/// transcript, the dictionary-corrected transcript, and how many dictionary words
/// were corrected. <see cref="CleanedTranscript"/> equals <see cref="RawTranscript"/>
/// byte-for-byte whenever no dictionary term matched.
/// </summary>
public sealed record DictationResult(string RawTranscript, string CleanedTranscript, int DictionaryWordsCorrected);

/// <summary>
/// The whole captured dictation clip as an uploadable WAV blob plus how long it was recorded
/// (issue #1130). Returned by <see cref="BatchDictationRecorder.StopAndGetWavAsync"/> so the durable
/// fire-and-forget Send can persist the bytes to disk before transcribing them.
/// </summary>
public sealed record CapturedAudio(byte[] Wav, long RecordingMs);

/// <summary>
/// Minimal RIFF/WAV container writer for raw PCM16. The desktop mic delivers raw
/// PCM that the transcription API cannot accept without a header, so the whole
/// captured clip is wrapped before the single batch upload. Delegates to the
/// shared <see cref="PcmWav"/> so the byte layout lives in exactly one place.
/// </summary>
internal static class WavWriter
{
    public static byte[] WrapPcm16(byte[]? pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        return PcmWav.Wrap(pcm, sampleRate, channels, bitsPerSample);
    }
}
