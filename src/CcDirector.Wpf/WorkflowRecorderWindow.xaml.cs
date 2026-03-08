using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowRecorderWindow : Window
{
    private readonly string _connectionName;
    private readonly int _daemonPort;
    private readonly List<WorkflowAction> _actions = new();
    private readonly List<WorkflowFile> _savedWorkflows = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private DispatcherTimer? _pollTimer;
    private string? _recordingSince;
    private DateTime _recordingStartTime;

    private enum RecorderState { Idle, Recording, Replaying }
    private RecorderState _state = RecorderState.Idle;

    public WorkflowRecorderWindow(string connectionName, int daemonPort)
    {
        InitializeComponent();

        _connectionName = connectionName;
        _daemonPort = daemonPort;

        TitleText.Text = $"WORKFLOW: {connectionName}";

        FileLog.Write($"[WorkflowRecorder] Created: connection={connectionName}, port={daemonPort}");

        // Position is set by the caller before Show()
        Loaded += (_, _) => LoadSavedWorkflows();
    }

    // -----------------------------------------------------------------------
    // Record / Stop / Clear
    // -----------------------------------------------------------------------

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowRecorder] BtnRecord_Click");

        _actions.Clear();
        UpdateActionLog();
        _recordingSince = DateTime.UtcNow.ToString("o");
        _recordingStartTime = DateTime.UtcNow;

        SetState(RecorderState.Recording);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollHistory();
        _pollTimer.Start();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write($"[WorkflowRecorder] BtnStop_Click: {_actions.Count} actions recorded");

        _pollTimer?.Stop();
        _pollTimer = null;

        SetState(RecorderState.Idle);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowRecorder] BtnClear_Click");
        _actions.Clear();
        UpdateActionLog();
    }

    // -----------------------------------------------------------------------
    // Save / Load / Replay
    // -----------------------------------------------------------------------

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var workflowName = WorkflowNameBox.Text.Trim();
        if (string.IsNullOrEmpty(workflowName))
        {
            MessageBox.Show("Enter a workflow name.", "Save Workflow",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_actions.Count == 0)
        {
            MessageBox.Show("No actions to save.", "Save Workflow",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FileLog.Write($"[WorkflowRecorder] BtnSave_Click: name={workflowName}, actions={_actions.Count}");

        var dir = CcStorage.ConnectionWorkflows(_connectionName);
        var safeName = string.Join("_", workflowName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(dir, $"{safeName}.json");

        var workflow = new
        {
            name = workflowName,
            connection = _connectionName,
            createdAt = DateTime.UtcNow.ToString("o"),
            actions = _actions.Select(a => new
            {
                command = a.Command,
                @params = a.Params,
            }).ToArray(),
        };

        var json = JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        FileLog.Write($"[WorkflowRecorder] Saved workflow: {filePath}");
        LoadSavedWorkflows();
    }

    private void LoadSavedWorkflows()
    {
        FileLog.Write($"[WorkflowRecorder] LoadSavedWorkflows: connection={_connectionName}");
        _savedWorkflows.Clear();
        WorkflowList.Items.Clear();

        var dir = CcStorage.ConnectionWorkflows(_connectionName);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileNameWithoutExtension(file) : Path.GetFileNameWithoutExtension(file);
                var actionCount = doc.TryGetProperty("actions", out var acts) ? acts.GetArrayLength() : 0;

                var wf = new WorkflowFile { Name = name, FilePath = file, ActionCount = actionCount };
                _savedWorkflows.Add(wf);
                WorkflowList.Items.Add($"{name} ({actionCount} actions)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowRecorder] Failed to load workflow {file}: {ex.Message}");
            }
        }

        FileLog.Write($"[WorkflowRecorder] Loaded {_savedWorkflows.Count} saved workflows");
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count)
        {
            BtnReplay.IsEnabled = false;
            return;
        }

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] Selected workflow: {wf.Name}");

        // Load actions from file into the action list
        try
        {
            var json = File.ReadAllText(wf.FilePath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.TryGetProperty("actions", out var acts))
            {
                _actions.Clear();
                foreach (var act in acts.EnumerateArray())
                {
                    var command = act.GetProperty("command").GetString() ?? "";
                    Dictionary<string, JsonElement>? parms = null;
                    if (act.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        parms = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetRawText());
                    }
                    _actions.Add(new WorkflowAction { Command = command, Params = parms });
                }
                UpdateActionLog();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] Failed to load workflow actions: {ex.Message}");
        }

        BtnReplay.IsEnabled = _actions.Count > 0 && _state != RecorderState.Replaying;
    }

    private async void BtnReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_actions.Count == 0) return;

        FileLog.Write($"[WorkflowRecorder] BtnReplay_Click: {_actions.Count} actions");
        SetState(RecorderState.Replaying);

        try
        {
            // Build batch commands
            var commands = _actions.Select(a =>
            {
                var cmd = new Dictionary<string, object> { ["command"] = a.Command };
                if (a.Params != null)
                {
                    foreach (var kv in a.Params)
                        cmd[kv.Key] = kv.Value;
                }
                return cmd;
            }).ToList();

            // Send in chunks of 50
            for (var i = 0; i < commands.Count; i += 50)
            {
                var chunk = commands.Skip(i).Take(50).ToList();
                var payload = new
                {
                    connection = _connectionName,
                    commands = chunk,
                    stopOnError = true,
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/batch", content);
                var result = await response.Content.ReadAsStringAsync();

                FileLog.Write($"[WorkflowRecorder] Replay batch {i / 50 + 1}: status={response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Replay failed at batch {i / 50 + 1}:\n{result}",
                        "Replay Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }
            }

            FileLog.Write("[WorkflowRecorder] Replay complete");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] Replay FAILED: {ex.Message}");
            MessageBox.Show($"Replay failed:\n{ex.Message}",
                "Replay Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SetState(RecorderState.Idle);
    }

    // -----------------------------------------------------------------------
    // Polling
    // -----------------------------------------------------------------------

    private async Task PollHistory()
    {
        try
        {
            var url = $"http://127.0.0.1:{_daemonPort}/history?connection={Uri.EscapeDataString(_connectionName)}";
            if (_recordingSince != null)
                url += $"&since={Uri.EscapeDataString(_recordingSince)}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            if (!data.TryGetProperty("actions", out var actions)) return;

            var newActions = new List<WorkflowAction>();
            foreach (var act in actions.EnumerateArray())
            {
                var command = act.GetProperty("command").GetString() ?? "";
                var timestamp = act.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;

                Dictionary<string, JsonElement>? parms = null;
                if (act.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    parms = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetRawText());
                }

                newActions.Add(new WorkflowAction
                {
                    Command = command,
                    Params = parms,
                    Timestamp = timestamp,
                });
            }

            // Only update if we have new actions beyond what we already have
            if (newActions.Count > _actions.Count)
            {
                _actions.Clear();
                _actions.AddRange(newActions);

                Dispatcher.Invoke(() =>
                {
                    UpdateActionLog();
                    var elapsed = (int)(DateTime.UtcNow - _recordingStartTime).TotalSeconds;
                    ElapsedText.Text = $"  |  Elapsed: {elapsed}s";
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] PollHistory FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // UI Helpers
    // -----------------------------------------------------------------------

    private void SetState(RecorderState state)
    {
        _state = state;
        switch (state)
        {
            case RecorderState.Idle:
                StatusText.Text = "IDLE";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
                BtnRecord.IsEnabled = true;
                BtnStop.IsEnabled = false;
                BtnReplay.IsEnabled = _actions.Count > 0;
                BtnSave.IsEnabled = true;
                break;

            case RecorderState.Recording:
                StatusText.Text = "RECORDING";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE5, 0x3E, 0x3E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = true;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                break;

            case RecorderState.Replaying:
                StatusText.Text = "REPLAYING";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = false;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                break;
        }

        ActionCountText.Text = $"Actions: {_actions.Count}";
    }

    private void UpdateActionLog()
    {
        var sb = new StringBuilder();
        foreach (var action in _actions)
        {
            var timeStr = "";
            if (!string.IsNullOrEmpty(action.Timestamp) &&
                DateTime.TryParse(action.Timestamp, out var dt))
            {
                timeStr = dt.ToLocalTime().ToString("HH:mm:ss") + " ";
            }

            sb.AppendLine($"{timeStr}{action.Command}");

            if (action.Params != null)
            {
                foreach (var kv in action.Params)
                {
                    var val = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString()
                        : kv.Value.GetRawText();
                    sb.AppendLine($"   -> {kv.Key}: {val}");
                }
            }
        }

        ActionLogText.Text = sb.ToString();
        ActionCountText.Text = $"Actions: {_actions.Count}";
        ActionLogScroll.ScrollToEnd();
    }

    // -----------------------------------------------------------------------
    // Data Classes
    // -----------------------------------------------------------------------

    private class WorkflowAction
    {
        public string Command { get; set; } = "";
        public Dictionary<string, JsonElement>? Params { get; set; }
        public string? Timestamp { get; set; }
    }

    private class WorkflowFile
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int ActionCount { get; set; }
    }
}
