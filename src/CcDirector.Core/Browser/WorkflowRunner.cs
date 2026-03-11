using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browser;

/// <summary>
/// Executes workflow steps sequentially, supporting actions and conditional branches.
/// Evaluates conditions via the daemon's /evaluate endpoint.
/// </summary>
public class WorkflowRunner
{
    private readonly string _connectionName;
    private readonly int _daemonPort;
    private readonly HttpClient _http;
    private readonly string _screenshotDir;
    private readonly Dictionary<string, string> _paramValues;

    private int _stepCounter;
    private bool _aborted;

    public List<WorkflowRunStep> CompletedSteps { get; } = new();
    public bool AllSucceeded { get; private set; } = true;

    /// <summary>Called after each step completes with (stepIndex, totalSteps, command).</summary>
    public Action<int, string>? OnStepProgress { get; set; }

    public WorkflowRunner(
        string connectionName,
        int daemonPort,
        HttpClient http,
        string screenshotDir,
        Dictionary<string, string> paramValues)
    {
        FileLog.Write($"[WorkflowRunner] Created: connection={connectionName}, port={daemonPort}");
        _connectionName = connectionName;
        _daemonPort = daemonPort;
        _http = http;
        _screenshotDir = screenshotDir;
        _paramValues = paramValues;
    }

    /// <summary>Execute a list of workflow steps.</summary>
    public async Task RunAsync(List<WorkflowStep> steps)
    {
        FileLog.Write($"[WorkflowRunner] RunAsync: {steps.Count} steps");
        _stepCounter = 0;
        _aborted = false;
        AllSucceeded = true;

        await ExecuteStepsAsync(steps);

        FileLog.Write($"[WorkflowRunner] RunAsync complete: {CompletedSteps.Count} steps executed, success={AllSucceeded}");
    }

    private async Task ExecuteStepsAsync(List<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            if (_aborted) return;

            if (step.Type == "condition" && step.Condition != null)
            {
                await ExecuteConditionAsync(step.Condition);
            }
            else if (step.Action != null)
            {
                await ExecuteActionAsync(step.Action);
            }
        }
    }

    private async Task ExecuteActionAsync(WorkflowAction action)
    {
        var index = _stepCounter++;
        var sw = Stopwatch.StartNew();

        var resolvedParams = ResolveParams(action.Params);

        var runStep = new WorkflowRunStep
        {
            Index = index,
            Command = action.Command,
            Params = resolvedParams,
            Timestamp = DateTime.UtcNow.ToString("o"),
        };

        OnStepProgress?.Invoke(index, action.Command);

        try
        {
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
            runStep.DurationMs = sw.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                runStep.Status = "failed";
                runStep.Error = $"HTTP {(int)response.StatusCode}: {errBody}";
                AllSucceeded = false;
                _aborted = true;
                FileLog.Write($"[WorkflowRunner] Step {index} FAILED: {runStep.Error}");
                CompletedSteps.Add(runStep);
                return;
            }

            runStep.Status = "completed";
            FileLog.Write($"[WorkflowRunner] Step {index} completed: {action.Command} in {runStep.DurationMs}ms");

            var ssFile = await CaptureScreenshotAsync(index);
            runStep.ScreenshotFile = ssFile;
        }
        catch (Exception ex)
        {
            sw.Stop();
            runStep.DurationMs = sw.ElapsedMilliseconds;
            runStep.Status = "failed";
            runStep.Error = ex.Message;
            AllSucceeded = false;
            _aborted = true;
            FileLog.Write($"[WorkflowRunner] Step {index} FAILED: {ex.Message}");
        }

        CompletedSteps.Add(runStep);
    }

    private async Task ExecuteConditionAsync(WorkflowCondition condition)
    {
        FileLog.Write($"[WorkflowRunner] Evaluating condition: check={condition.Check}, selector={condition.Selector}, value={condition.Value}");

        var js = BuildConditionJs(condition);
        var result = await EvaluateJsAsync(js);

        FileLog.Write($"[WorkflowRunner] Condition result: {result}");

        if (result)
        {
            FileLog.Write($"[WorkflowRunner] Condition TRUE: executing {condition.ThenSteps.Count} then-steps");
            await ExecuteStepsAsync(condition.ThenSteps);
        }
        else
        {
            FileLog.Write($"[WorkflowRunner] Condition FALSE: executing {condition.ElseSteps.Count} else-steps");
            await ExecuteStepsAsync(condition.ElseSteps);
        }
    }

    internal static string BuildConditionJs(WorkflowCondition condition)
    {
        return condition.Check switch
        {
            "elementExists" => $"!!document.querySelector('{EscapeJs(condition.Selector ?? "")}')",
            "urlContains" => $"window.location.href.includes('{EscapeJs(condition.Value ?? "")}')",
            "textVisible" => $"!!document.querySelector('body').innerText.includes('{EscapeJs(condition.Value ?? "")}')",
            _ => "false",
        };
    }

    private async Task<bool> EvaluateJsAsync(string jsExpression)
    {
        FileLog.Write($"[WorkflowRunner] EvaluateJsAsync: {jsExpression}");

        var payload = JsonSerializer.Serialize(new
        {
            connection = _connectionName,
            fn = jsExpression,
        });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/evaluate", content);

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[WorkflowRunner] EvaluateJs FAILED: HTTP {(int)response.StatusCode}");
            return false;
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        if (doc.TryGetProperty("result", out var resultEl))
        {
            if (resultEl.ValueKind == JsonValueKind.True) return true;
            if (resultEl.ValueKind == JsonValueKind.False) return false;
            var str = resultEl.ToString();
            return str == "true" || str == "True";
        }

        return false;
    }

    private async Task<string?> CaptureScreenshotAsync(int stepIndex)
    {
        var payload = JsonSerializer.Serialize(new { connection = _connectionName });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/screenshot", content);

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[WorkflowRunner] Screenshot FAILED: HTTP {(int)response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        if (!doc.TryGetProperty("data", out var dataEl))
        {
            FileLog.Write("[WorkflowRunner] Screenshot response missing 'data' field");
            return null;
        }

        var base64 = dataEl.GetString();
        if (string.IsNullOrEmpty(base64))
        {
            FileLog.Write("[WorkflowRunner] Screenshot data is empty");
            return null;
        }

        var fileName = $"step-{stepIndex + 1:D3}.jpg";
        var filePath = Path.Combine(_screenshotDir, fileName);
        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(filePath, bytes);

        FileLog.Write($"[WorkflowRunner] Screenshot saved: {fileName} ({bytes.Length} bytes)");
        return fileName;
    }

    private Dictionary<string, object>? ResolveParams(Dictionary<string, object>? templateParams)
    {
        if (templateParams == null || templateParams.Count == 0)
            return templateParams;

        var resolved = new Dictionary<string, object>();
        foreach (var kv in templateParams)
        {
            var strVal = kv.Value?.ToString() ?? "";
            foreach (var pv in _paramValues)
            {
                strVal = strVal.Replace($"{{{pv.Key}}}", pv.Value);
            }
            resolved[kv.Key] = strVal;
        }

        return resolved;
    }

    private static string EscapeJs(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }
}
