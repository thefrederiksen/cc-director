using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using VoiceChat.Core.Logging;
using VoiceChat.Core.Models;
using VoiceChat.Core.Pipeline;
using VoiceChat.Core.Recording;

namespace VoiceChat.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VoicePipeline _pipeline;
    private CancellationTokenSource? _processingCts;

    private string _statusText = "Starting up...";
    private string _selectedVoice = "af_heart";
    private string _selectedSttEngine = "Vosk Small EN";
    private string _partialTranscription = string.Empty;
    private bool _isRecording;
    private bool _isProcessing;
    private bool _isInitialized;
    private bool _isSwitchingSttEngine;
    private string _sttLatency = "--";
    private string _llmLatency = "--";
    private string _ttsLatency = "--";
    private string _totalLatency = "--";
    private string _dictionaryText = string.Empty;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<string> Voices { get; } = [];
    public ObservableCollection<string> SttEngines { get; } = [];

    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
    public bool IsRecording { get => _isRecording; set => SetField(ref _isRecording, value); }
    public bool IsProcessing { get => _isProcessing; set => SetField(ref _isProcessing, value); }
    public bool IsInitialized { get => _isInitialized; set => SetField(ref _isInitialized, value); }
    public bool IsSwitchingSttEngine { get => _isSwitchingSttEngine; set => SetField(ref _isSwitchingSttEngine, value); }
    public string SttLatency { get => _sttLatency; set => SetField(ref _sttLatency, value); }
    public string LlmLatency { get => _llmLatency; set => SetField(ref _llmLatency, value); }
    public string TtsLatency { get => _ttsLatency; set => SetField(ref _ttsLatency, value); }
    public string TotalLatency { get => _totalLatency; set => SetField(ref _totalLatency, value); }

    public string PartialTranscription
    {
        get => _partialTranscription;
        set => SetField(ref _partialTranscription, value);
    }

    public string DictionaryText
    {
        get => _dictionaryText;
        set
        {
            if (SetField(ref _dictionaryText, value))
            {
                var words = value
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .ToArray();
                _pipeline.Dictionary.SetWords(words);
                VoiceLog.Write($"[MainViewModel] DictionaryText updated: {words.Length} words");
            }
        }
    }

    public string SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetField(ref _selectedVoice, value))
                _pipeline.SelectedVoice = value;
        }
    }

    public string SelectedSttEngine
    {
        get => _selectedSttEngine;
        set
        {
            if (SetField(ref _selectedSttEngine, value) && IsInitialized)
            {
                _ = SwitchSttEngineAsync(value);
            }
        }
    }

    public ICommand ResetCommand { get; }

    public MainViewModel()
    {
        VoiceLog.Write("[MainViewModel] Creating.");
        _pipeline = new VoicePipeline();

        _pipeline.StatusChanged += status =>
            Application.Current.Dispatcher.BeginInvoke(() => StatusText = status);

        _pipeline.UserMessageReady += msg =>
            Application.Current.Dispatcher.BeginInvoke(() => Messages.Add(msg));

        _pipeline.AssistantMessageReady += msg =>
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Messages.Add(msg);
                UpdateLatencyDisplay(msg.Latency);
            });

        _pipeline.PartialTranscriptionChanged += partial =>
            Application.Current.Dispatcher.BeginInvoke(() => PartialTranscription = partial);

        ResetCommand = new RelayCommand(ResetConversation);
    }

    public async Task InitializeAsync()
    {
        VoiceLog.Write("[MainViewModel] InitializeAsync: starting pipeline init.");
        try
        {
            await _pipeline.InitializeAsync();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var voice in _pipeline.GetAvailableVoices())
                    Voices.Add(voice);

                foreach (var engine in _pipeline.GetSttEngines())
                    SttEngines.Add(engine);

                _selectedSttEngine = _pipeline.GetCurrentSttEngine();
                _dictionaryText = string.Join("\n", _pipeline.Dictionary.GetWords());

                // Re-trigger properties after populating so ComboBoxes pick them up
                OnPropertyChanged(nameof(SelectedVoice));
                OnPropertyChanged(nameof(SelectedSttEngine));
                OnPropertyChanged(nameof(DictionaryText));
                IsInitialized = true;
            });

            VoiceLog.Write("[MainViewModel] InitializeAsync: complete. IsInitialized=true");
            VoiceLog.Write($"[MainViewModel] Recordings dir: {AudioLibrary.RecordingsPath}");
            VoiceLog.Write($"[MainViewModel] Log file: {VoiceLog.CurrentLogPath}");
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"[MainViewModel] InitializeAsync FAILED: {ex}");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                StatusText = $"Init failed: {ex.Message}");
        }
    }

    private async Task SwitchSttEngineAsync(string engineName)
    {
        VoiceLog.Write($"[MainViewModel] SwitchSttEngineAsync: {engineName}");
        IsSwitchingSttEngine = true;
        try
        {
            await _pipeline.SetSttEngineAsync(engineName);
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"[MainViewModel] SwitchSttEngineAsync FAILED: {ex}");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                StatusText = $"Engine switch failed: {ex.Message}");
        }
        finally
        {
            IsSwitchingSttEngine = false;
        }
    }

    public void StartRecording()
    {
        if (!IsInitialized || IsProcessing || IsSwitchingSttEngine) return;

        VoiceLog.Write("[MainViewModel] StartRecording.");
        IsRecording = true;
        PartialTranscription = string.Empty;
        _pipeline.StartRecording();
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        VoiceLog.Write("[MainViewModel] StopRecording -> processing.");
        IsRecording = false;
        IsProcessing = true;

        _processingCts = new CancellationTokenSource();
        try
        {
            await _pipeline.StopRecordingAndProcessAsync(_processingCts.Token);
        }
        catch (OperationCanceledException)
        {
            VoiceLog.Write("[MainViewModel] Processing cancelled.");
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"[MainViewModel] Processing FAILED: {ex}");
            StatusText = $"ERROR: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            PartialTranscription = string.Empty;
            _processingCts?.Dispose();
            _processingCts = null;
        }
    }

    private void UpdateLatencyDisplay(LatencyInfo? latency)
    {
        if (latency is null) return;
        SttLatency = $"{latency.SttMs} ms";
        LlmLatency = $"{latency.LlmMs} ms";
        TtsLatency = $"{latency.TtsMs} ms";
        TotalLatency = $"{latency.TotalMs} ms";
    }

    private void ResetConversation()
    {
        VoiceLog.Write("[MainViewModel] ResetConversation.");
        Messages.Clear();
        _pipeline.ResetConversation();
        SttLatency = "--";
        LlmLatency = "--";
        TtsLatency = "--";
        TotalLatency = "--";
    }

    public void Dispose()
    {
        VoiceLog.Write("[MainViewModel] Disposing.");
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _pipeline.Dispose();
    }
}
