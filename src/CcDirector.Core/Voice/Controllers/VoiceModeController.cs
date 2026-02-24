using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;
using CcDirector.Core.Voice.Models;
using CcDirector.Core.Voice.Services;

namespace CcDirector.Core.Voice.Controllers;

/// <summary>
/// Orchestrates the voice mode flow:
/// Record -> Transcribe -> Send to Claude -> Wait -> Extract -> Summarize -> Speak
/// Supports both batch and streaming transcription.
/// </summary>
public class VoiceModeController : IDisposable
{
    private readonly IAudioRecorder _audioRecorder;
    private readonly ISpeechToText _speechToText;
    private readonly IStreamingSpeechToText? _streamingStt;
    private readonly IResponseSummarizer _summarizer;
    private readonly ITextToSpeech _textToSpeech;
    private readonly Action<string> _playAudioCallback;

    private Session? _activeSession;
    private CancellationTokenSource? _cts;
    private VoiceState _state = VoiceState.Idle;
    private string? _lastError;
    private bool _disposed;
    private bool _useStreaming;

    /// <summary>
    /// Current state of the voice mode.
    /// </summary>
    public VoiceState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                var oldState = _state;
                _state = value;
                FileLog.Write($"[VoiceModeController] State changed: {oldState} -> {value}");
                OnStateChanged?.Invoke(oldState, value);
            }
        }
    }

    /// <summary>
    /// Last error message if state is Error.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Fires when the voice state changes.
    /// </summary>
    public event Action<VoiceState, VoiceState>? OnStateChanged;

    /// <summary>
    /// Fires when transcription is complete.
    /// </summary>
    public event Action<string>? OnTranscriptionComplete;

    /// <summary>
    /// Fires when summary is ready.
    /// </summary>
    public event Action<string>? OnSummaryReady;

    /// <summary>
    /// Fires when partial transcription is available (streaming mode only).
    /// </summary>
    public event Action<string>? OnPartialTranscription;

    /// <summary>
    /// Whether streaming transcription is being used.
    /// </summary>
    public bool IsStreamingMode => _useStreaming;

    /// <summary>
    /// Creates a new VoiceModeController.
    /// </summary>
    /// <param name="audioRecorder">Audio recorder implementation.</param>
    /// <param name="speechToText">Speech-to-text implementation (batch mode fallback).</param>
    /// <param name="summarizer">Response summarizer implementation.</param>
    /// <param name="textToSpeech">Text-to-speech implementation.</param>
    /// <param name="playAudioCallback">Callback to play audio file.</param>
    /// <param name="streamingStt">Optional streaming speech-to-text for real-time transcription.</param>
    public VoiceModeController(
        IAudioRecorder audioRecorder,
        ISpeechToText speechToText,
        IResponseSummarizer summarizer,
        ITextToSpeech textToSpeech,
        Action<string> playAudioCallback,
        IStreamingSpeechToText? streamingStt = null)
    {
        _audioRecorder = audioRecorder;
        _speechToText = speechToText;
        _summarizer = summarizer;
        _textToSpeech = textToSpeech;
        _playAudioCallback = playAudioCallback;
        _streamingStt = streamingStt;

        // Use streaming if available
        _useStreaming = streamingStt?.IsAvailable == true;
        if (_useStreaming)
        {
            FileLog.Write($"[VoiceModeController] Using streaming STT: {streamingStt!.ModelPath}");
            _streamingStt!.OnPartialResult += OnStreamingPartialResult;
            _audioRecorder.OnAudioDataAvailable += OnAudioDataForStreaming;
        }
        else
        {
            FileLog.Write("[VoiceModeController] Using batch STT");
        }
    }

    private void OnStreamingPartialResult(string partialText)
    {
        FileLog.Write($"[VoiceModeController] Partial: {partialText}");
        OnPartialTranscription?.Invoke(partialText);
    }

    private void OnAudioDataForStreaming(byte[] audioData)
    {
        if (State == VoiceState.Recording && _streamingStt != null)
        {
            _streamingStt.ProcessAudioChunk(audioData);
        }
    }

    /// <summary>
    /// Set the active session for voice mode.
    /// </summary>
    public void SetSession(Session? session)
    {
        _activeSession = session;
        FileLog.Write($"[VoiceModeController] SetSession: {session?.Id}");
    }

    /// <summary>
    /// Toggle voice recording. If idle, starts recording. If recording, stops and processes.
    /// </summary>
    public void ToggleRecording()
    {
        if (_disposed) return;

        if (State == VoiceState.Recording)
        {
            StopRecording();
        }
        else if (State == VoiceState.Idle)
        {
            StartRecording();
        }
        else
        {
            FileLog.Write($"[VoiceModeController] ToggleRecording ignored, state={State}");
        }
    }

    /// <summary>
    /// Start recording audio.
    /// </summary>
    public void StartRecording()
    {
        if (_disposed) return;
        if (!_audioRecorder.IsAvailable)
        {
            SetError(_audioRecorder.UnavailableReason ?? "Microphone not available");
            return;
        }

        if (_activeSession == null)
        {
            SetError("No active session");
            return;
        }

        FileLog.Write($"[VoiceModeController] StartRecording (streaming={_useStreaming})");
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _lastError = null;

        try
        {
            // Start streaming session if enabled
            if (_useStreaming && _streamingStt != null)
            {
                _streamingStt.StartSession();
            }

            _audioRecorder.StartRecording();
            State = VoiceState.Recording;
        }
        catch (Exception ex)
        {
            SetError($"Failed to start recording: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop recording and start processing the audio.
    /// </summary>
    public void StopRecording()
    {
        if (_disposed) return;
        if (State != VoiceState.Recording) return;

        FileLog.Write("[VoiceModeController] StopRecording");
        _ = ProcessRecordingAsync();
    }

    /// <summary>
    /// Cancel the current voice operation and return to idle.
    /// </summary>
    public void Cancel()
    {
        FileLog.Write("[VoiceModeController] Cancel");
        _cts?.Cancel();
        State = VoiceState.Idle;
    }

    /// <summary>
    /// Process the voice flow after recording stops.
    /// </summary>
    private async Task ProcessRecordingAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            // Stop recording and get the audio file
            var audioPath = await _audioRecorder.StopRecordingAsync();
            FileLog.Write($"[VoiceModeController] Audio recorded: {audioPath}");

            // Transcribe - use streaming result if available, otherwise batch
            State = VoiceState.Transcribing;
            string transcription;

            if (_useStreaming && _streamingStt != null)
            {
                // End streaming session and get final transcription
                transcription = _streamingStt.EndSession();
                FileLog.Write($"[VoiceModeController] Streaming transcription: {transcription}");
            }
            else
            {
                // Batch transcription
                transcription = await _speechToText.TranscribeAsync(audioPath, ct);
                FileLog.Write($"[VoiceModeController] Batch transcription: {transcription}");
            }

            OnTranscriptionComplete?.Invoke(transcription);

            if (string.IsNullOrWhiteSpace(transcription))
            {
                SetError("No speech detected");
                return;
            }

            // Send to Claude
            if (_activeSession == null)
            {
                SetError("No active session");
                return;
            }

            State = VoiceState.WaitingForClaude;
            await _activeSession.SendTextAsync(transcription);

            // Wait for Claude to finish (ActivityState becomes WaitingForInput)
            await WaitForClaudeResponseAsync(ct);

            // Extract the response
            var jsonlPath = ClaudeSessionReader.GetJsonlPath(
                _activeSession.ClaudeSessionId ?? "",
                _activeSession.RepoPath);
            var response = ClaudeResponseExtractor.ExtractLastResponse(jsonlPath);

            if (string.IsNullOrEmpty(response))
            {
                SetError("No response from Claude");
                return;
            }

            FileLog.Write($"[VoiceModeController] Claude response: {response.Length} chars");

            // Summarize
            State = VoiceState.Summarizing;
            var summary = await _summarizer.SummarizeAsync(response, ct);
            FileLog.Write($"[VoiceModeController] Summary: {summary}");
            OnSummaryReady?.Invoke(summary);

            // Synthesize and play
            State = VoiceState.Speaking;
            var ttsPath = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.wav");
            await _textToSpeech.SynthesizeAsync(summary, ttsPath, ct);

            _playAudioCallback(ttsPath);

            // Return to idle after playback starts
            State = VoiceState.Idle;
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[VoiceModeController] Operation cancelled");
            State = VoiceState.Idle;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceModeController] ProcessRecordingAsync FAILED: {ex}");
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Wait for Claude to finish processing (state becomes WaitingForInput).
    /// </summary>
    private async Task WaitForClaudeResponseAsync(CancellationToken ct)
    {
        if (_activeSession == null) return;

        var tcs = new TaskCompletionSource();

        void OnStateChange(ActivityState oldState, ActivityState newState)
        {
            if (newState == ActivityState.WaitingForInput)
            {
                tcs.TrySetResult();
            }
        }

        _activeSession.OnActivityStateChanged += OnStateChange;

        try
        {
            // If already waiting for input, return immediately
            if (_activeSession.ActivityState == ActivityState.WaitingForInput)
            {
                return;
            }

            // Wait with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        finally
        {
            _activeSession.OnActivityStateChanged -= OnStateChange;
        }
    }

    private void SetError(string message)
    {
        FileLog.Write($"[VoiceModeController] Error: {message}");
        _lastError = message;
        State = VoiceState.Error;

        // Auto-reset to idle after a moment
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            if (State == VoiceState.Error)
                State = VoiceState.Idle;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
    }
}
