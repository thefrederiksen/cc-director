using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CcDirector.Core.Browser;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowRecorderWindow : Window
{
    private readonly string _connectionName;
    private readonly int _daemonPort;
    private readonly List<RecordedAction> _actions = new();
    private readonly List<WorkflowFile> _savedWorkflows = new();
    private readonly WorkflowStore _store = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private DispatcherTimer? _pollTimer;
    private string? _recordingSince;
    private DateTime _recordingStartTime;
    private string? _recordingTempDir;
    private int _lastScreenshotCount;

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

    private async void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowRecorder] BtnRecord_Click");

        _actions.Clear();
        UpdateActionLog();
        _recordingSince = DateTime.UtcNow.ToString("o");
        _recordingStartTime = DateTime.UtcNow;
        _lastScreenshotCount = 0;

        // Create temp directory for recording screenshots
        _recordingTempDir = Path.Combine(Path.GetTempPath(), $"wf-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingTempDir);
        FileLog.Write($"[WorkflowRecorder] Recording temp dir: {_recordingTempDir}");

        // Tell the browser extension to start capturing user actions
        try
        {
            var payload = JsonSerializer.Serialize(new { connection = _connectionName });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/record/start", content);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                FileLog.Write($"[WorkflowRecorder] record/start FAILED: {err}");
                MessageBox.Show($"Failed to start recording:\n{err}",
                    "Recording Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FileLog.Write("[WorkflowRecorder] record/start OK");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] record/start FAILED: {ex.Message}");
            MessageBox.Show($"Failed to start recording:\n{ex.Message}",
                "Recording Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetState(RecorderState.Recording);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) =>
        {
            try { await PollHistory(); }
            catch (Exception ex) { FileLog.Write($"[WorkflowRecorder] PollTimer FAILED: {ex.Message}"); }
        };
        _pollTimer.Start();
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write($"[WorkflowRecorder] BtnStop_Click: {_actions.Count} actions recorded");

        _pollTimer?.Stop();
        _pollTimer = null;

        // Tell the browser extension to stop capturing user actions
        try
        {
            var payload = JsonSerializer.Serialize(new { connection = _connectionName });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/record/stop", content);
            FileLog.Write("[WorkflowRecorder] record/stop OK");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] record/stop FAILED: {ex.Message}");
        }

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

        // Build the template
        var template = new WorkflowTemplate
        {
            Name = workflowName,
            Connection = _connectionName,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };

        for (var i = 0; i < _actions.Count; i++)
        {
            var a = _actions[i];
            var screenshotFile = a.ScreenshotFile;

            template.Actions.Add(new WorkflowAction
            {
                Command = a.Command,
                Params = ConvertParams(a.Params),
                ScreenshotFile = screenshotFile,
            });
        }

        _store.SaveTemplate(template);

        // Move recording screenshots from temp to permanent location
        if (_recordingTempDir != null && Directory.Exists(_recordingTempDir))
        {
            var recordingDir = _store.RecordingDir(_connectionName, workflowName);
            foreach (var file in Directory.GetFiles(_recordingTempDir, "*.jpg"))
            {
                var dest = Path.Combine(recordingDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
            FileLog.Write($"[WorkflowRecorder] Copied {Directory.GetFiles(recordingDir, "*.jpg").Length} screenshots to recording dir");

            // Clean up temp
            try { Directory.Delete(_recordingTempDir, recursive: true); }
            catch (Exception ex) { FileLog.Write($"[WorkflowRecorder] Temp dir cleanup failed: {ex.Message}"); }
            _recordingTempDir = null;
        }

        FileLog.Write($"[WorkflowRecorder] Workflow saved: {workflowName}");
        LoadSavedWorkflows();
    }

    private void LoadSavedWorkflows()
    {
        FileLog.Write($"[WorkflowRecorder] LoadSavedWorkflows: connection={_connectionName}");
        _savedWorkflows.Clear();
        WorkflowList.Items.Clear();

        var templates = _store.ListTemplates(_connectionName);
        foreach (var t in templates)
        {
            var paramCount = t.Parameters.Count;
            var suffix = paramCount > 0 ? $" [{paramCount} params]" : "";
            var wf = new WorkflowFile
            {
                Name = t.Name,
                ActionCount = t.Actions.Count,
                ParamCount = paramCount,
            };
            _savedWorkflows.Add(wf);
            WorkflowList.Items.Add($"{t.Name} ({t.Actions.Count} actions{suffix})");
        }

        FileLog.Write($"[WorkflowRecorder] Loaded {_savedWorkflows.Count} saved workflows");
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count)
        {
            BtnReplay.IsEnabled = false;
            BtnParameterize.IsEnabled = false;
            BtnRuns.IsEnabled = false;
            return;
        }

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] Selected workflow: {wf.Name}");

        // Load actions from template into the action list for display
        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template != null)
        {
            _actions.Clear();
            foreach (var action in template.Actions)
            {
                _actions.Add(new RecordedAction
                {
                    Command = action.Command,
                    Params = ConvertParamsToJsonElement(action.Params),
                    ScreenshotFile = action.ScreenshotFile,
                });
            }
            UpdateActionLog();
        }

        BtnReplay.IsEnabled = _actions.Count > 0 && _state != RecorderState.Replaying;
        BtnParameterize.IsEnabled = _actions.Count > 0 && _state == RecorderState.Idle;
        BtnRuns.IsEnabled = _state == RecorderState.Idle;
    }

    // -----------------------------------------------------------------------
    // Parameterize
    // -----------------------------------------------------------------------

    private void BtnParameterize_Click(object sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnParameterize_Click: {wf.Name}");

        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template == null) return;

        var dialog = new WorkflowParameterizeDialog(template);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.Saved)
        {
            _store.SaveTemplate(template);
            FileLog.Write($"[WorkflowRecorder] Template parameterized: {template.Parameters.Count} params");
            LoadSavedWorkflows();
        }
    }

    // -----------------------------------------------------------------------
    // Runs dialog
    // -----------------------------------------------------------------------

    private void BtnRuns_Click(object sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnRuns_Click: {wf.Name}");

        var dialog = new WorkflowRunsDialog(_store, _connectionName, wf.Name);
        dialog.Owner = this;
        dialog.Show();
    }

    // -----------------------------------------------------------------------
    // Sequential Replay with Run Records
    // -----------------------------------------------------------------------

    private async void BtnReplay_Click(object sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnReplay_Click: {wf.Name}, {_actions.Count} actions");

        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template == null || template.Actions.Count == 0) return;

        // If parameterized, prompt for values
        var paramValues = new Dictionary<string, string>();
        if (template.Parameters.Count > 0)
        {
            var paramDialog = new WorkflowParametersDialog(template.Parameters);
            paramDialog.Owner = this;
            if (paramDialog.ShowDialog() != true)
                return;
            paramValues = paramDialog.ResolvedValues;
        }

        SetState(RecorderState.Replaying);

        // Create run record
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
        var run = new WorkflowRun
        {
            Id = runId,
            WorkflowName = wf.Name,
            Connection = _connectionName,
            StartedAt = DateTime.UtcNow.ToString("o"),
            Status = "running",
            ParameterValues = paramValues,
        };

        var ssDir = _store.RunScreenshotDir(_connectionName, wf.Name, runId);
        FileLog.Write($"[WorkflowRecorder] Run started: id={runId}, ssDir={ssDir}");

        var allSucceeded = true;

        for (var i = 0; i < template.Actions.Count; i++)
        {
            var action = template.Actions[i];
            var sw = Stopwatch.StartNew();

            // Resolve parameter placeholders
            var resolvedParams = ResolveParams(action.Params, paramValues);

            var step = new WorkflowRunStep
            {
                Index = i,
                Command = action.Command,
                Params = resolvedParams,
                Timestamp = DateTime.UtcNow.ToString("o"),
            };

            // Update status display
            _ = Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = $"STEP {i + 1}/{template.Actions.Count}";
                ActionCountText.Text = $"Running: {action.Command}";
            });

            try
            {
                // Send single command to daemon
                var cmdPayload = new Dictionary<string, object>
                {
                    ["connection"] = _connectionName,
                    ["command"] = action.Command,
                };
                if (resolvedParams != null)
                {
                    foreach (var kv in resolvedParams)
                        cmdPayload[kv.Key] = kv.Value;
                }

                var json = JsonSerializer.Serialize(cmdPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(
                    $"http://127.0.0.1:{_daemonPort}/{action.Command}", content);

                sw.Stop();
                step.DurationMs = sw.ElapsedMilliseconds;

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    step.Status = "failed";
                    step.Error = $"HTTP {(int)response.StatusCode}: {errBody}";
                    allSucceeded = false;
                    FileLog.Write($"[WorkflowRecorder] Step {i} FAILED: {step.Error}");
                    run.Steps.Add(step);
                    break;
                }

                step.Status = "completed";
                FileLog.Write($"[WorkflowRecorder] Step {i} completed: {action.Command} in {step.DurationMs}ms");

                // Take screenshot after step
                var ssFile = await CaptureScreenshotAsync(ssDir, i);
                step.ScreenshotFile = ssFile;
            }
            catch (Exception ex)
            {
                sw.Stop();
                step.DurationMs = sw.ElapsedMilliseconds;
                step.Status = "failed";
                step.Error = ex.Message;
                allSucceeded = false;
                FileLog.Write($"[WorkflowRecorder] Step {i} FAILED: {ex.Message}");
                run.Steps.Add(step);
                break;
            }

            run.Steps.Add(step);
        }

        run.CompletedAt = DateTime.UtcNow.ToString("o");
        run.Status = allSucceeded ? "completed" : "failed";

        _store.SaveRun(run);
        FileLog.Write($"[WorkflowRecorder] Run finished: id={runId}, status={run.Status}, steps={run.Steps.Count}");

        SetState(RecorderState.Idle);

        var resultMsg = allSucceeded
            ? $"Workflow completed: {run.Steps.Count} steps"
            : $"Workflow failed at step {run.Steps.Count}";
        MessageBox.Show(resultMsg, "Workflow Run", MessageBoxButton.OK,
            allSucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // -----------------------------------------------------------------------
    // Screenshots
    // -----------------------------------------------------------------------

    private async Task<string?> CaptureScreenshotAsync(string targetDir, int stepIndex)
    {
        var payload = JsonSerializer.Serialize(new { connection = _connectionName });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/screenshot", content);

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[WorkflowRecorder] Screenshot FAILED: HTTP {(int)response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        if (!doc.TryGetProperty("data", out var dataEl))
        {
            FileLog.Write("[WorkflowRecorder] Screenshot response missing 'data' field");
            return null;
        }

        var base64 = dataEl.GetString();
        if (string.IsNullOrEmpty(base64))
        {
            FileLog.Write("[WorkflowRecorder] Screenshot data is empty");
            return null;
        }

        var fileName = $"step-{stepIndex + 1:D3}.jpg";
        var filePath = Path.Combine(targetDir, fileName);
        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(filePath, bytes);

        FileLog.Write($"[WorkflowRecorder] Screenshot saved: {fileName} ({bytes.Length} bytes)");
        return fileName;
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

            var newActions = new List<RecordedAction>();
            foreach (var act in actions.EnumerateArray())
            {
                var command = act.GetProperty("command").GetString() ?? "";
                var timestamp = act.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;

                Dictionary<string, JsonElement>? parms = null;
                if (act.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    parms = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetRawText());
                }

                newActions.Add(new RecordedAction
                {
                    Command = command,
                    Params = parms,
                    Timestamp = timestamp,
                });
            }

            // Only update if we have new actions beyond what we already have
            if (newActions.Count > _actions.Count)
            {
                var previousCount = _actions.Count;
                _actions.Clear();
                _actions.AddRange(newActions);

                // Capture screenshots for new actions
                if (_recordingTempDir != null)
                {
                    for (var i = _lastScreenshotCount; i < _actions.Count; i++)
                    {
                        try
                        {
                            var ssFile = await CaptureScreenshotAsync(_recordingTempDir, i);
                            _actions[i].ScreenshotFile = ssFile;
                        }
                        catch (Exception ssEx)
                        {
                            FileLog.Write($"[WorkflowRecorder] Recording screenshot {i} FAILED: {ssEx.Message}");
                        }
                    }
                    _lastScreenshotCount = _actions.Count;
                }

                _ = Dispatcher.BeginInvoke(() =>
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
                BtnParameterize.IsEnabled = WorkflowList.SelectedIndex >= 0;
                BtnRuns.IsEnabled = WorkflowList.SelectedIndex >= 0;
                break;

            case RecorderState.Recording:
                StatusText.Text = "RECORDING";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE5, 0x3E, 0x3E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = true;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                BtnParameterize.IsEnabled = false;
                BtnRuns.IsEnabled = false;
                break;

            case RecorderState.Replaying:
                StatusText.Text = "REPLAYING";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = false;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                BtnParameterize.IsEnabled = false;
                BtnRuns.IsEnabled = false;
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

            var ssIndicator = action.ScreenshotFile != null ? " [SS]" : "";
            sb.AppendLine($"{timeStr}{action.Command}{ssIndicator}");

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
    // Parameter resolution
    // -----------------------------------------------------------------------

    private static Dictionary<string, object>? ResolveParams(
        Dictionary<string, object>? templateParams,
        Dictionary<string, string> values)
    {
        if (templateParams == null || templateParams.Count == 0)
            return templateParams;

        var resolved = new Dictionary<string, object>();
        foreach (var kv in templateParams)
        {
            var strVal = kv.Value?.ToString() ?? "";

            // Replace all {var} placeholders
            foreach (var pv in values)
            {
                strVal = strVal.Replace($"{{{pv.Key}}}", pv.Value);
            }

            resolved[kv.Key] = strVal;
        }

        return resolved;
    }

    // -----------------------------------------------------------------------
    // Conversion helpers
    // -----------------------------------------------------------------------

    private static Dictionary<string, object>? ConvertParams(Dictionary<string, JsonElement>? jsonParams)
    {
        if (jsonParams == null) return null;

        var result = new Dictionary<string, object>();
        foreach (var kv in jsonParams)
        {
            result[kv.Key] = kv.Value.ValueKind == JsonValueKind.String
                ? kv.Value.GetString() ?? kv.Value.GetRawText()
                : kv.Value.GetRawText();
        }

        return result;
    }

    private static Dictionary<string, JsonElement>? ConvertParamsToJsonElement(Dictionary<string, object>? objParams)
    {
        if (objParams == null) return null;

        var json = JsonSerializer.Serialize(objParams);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    // -----------------------------------------------------------------------
    // Data Classes
    // -----------------------------------------------------------------------

    private class RecordedAction
    {
        public string Command { get; set; } = "";
        public Dictionary<string, JsonElement>? Params { get; set; }
        public string? Timestamp { get; set; }
        public string? ScreenshotFile { get; set; }
    }

    private class WorkflowFile
    {
        public string Name { get; set; } = "";
        public int ActionCount { get; set; }
        public int ParamCount { get; set; }
    }
}
