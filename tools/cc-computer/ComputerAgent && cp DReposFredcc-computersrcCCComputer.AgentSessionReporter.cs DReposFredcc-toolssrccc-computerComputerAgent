using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace CCComputer.Agent;

/// <summary>
/// Manages the lifecycle of one evidence chain per request.
/// Records every action, screenshot, and LLM observation for auditing.
/// </summary>
public class EvidenceChainLogger : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EvidenceChain _chain;
    private readonly string _sessionFolder;
    private readonly Stopwatch _totalStopwatch;

    private EvidenceStep? _currentStep;
    private Stopwatch? _stepStopwatch;
    private ToolAction? _currentAction;
    private Stopwatch? _actionStopwatch;
    private bool _finalized;
    private bool _disposed;

    public EvidenceChainLogger(string sessionFolder, string sessionId, string request, string modelId, int settleDelayMs)
    {
        _sessionFolder = sessionFolder;
        _totalStopwatch = Stopwatch.StartNew();

        _chain = new EvidenceChain
        {
            SessionId = sessionId,
            StartedAt = DateTime.UtcNow,
            Request = request,
            Config = new EvidenceConfig
            {
                ModelId = modelId,
                SettleDelayMs = settleDelayMs
            }
        };
    }

    /// <summary>
    /// Start a new step before tool execution.
    /// </summary>
    public void BeginStep()
    {
        _currentStep = new EvidenceStep
        {
            StepNumber = _chain.Steps.Count + 1,
            StepStartedAt = DateTime.UtcNow
        };
        _stepStopwatch = Stopwatch.StartNew();
        _chain.Steps.Add(_currentStep);
    }

    /// <summary>
    /// Record the start of a tool invocation.
    /// </summary>
    public void RecordActionStart(string toolName, Dictionary<string, object?> arguments)
    {
        if (_currentStep == null) return;

        _currentAction = new ToolAction
        {
            ToolName = toolName,
            Arguments = arguments,
            StartedAt = DateTime.UtcNow
        };
        _actionStopwatch = Stopwatch.StartNew();
        _currentStep.Actions.Add(_currentAction);
    }

    /// <summary>
    /// Record the completion of the current tool invocation.
    /// </summary>
    public void RecordActionComplete(string result, bool success, string? errorMessage = null)
    {
        if (_currentAction == null) return;

        _actionStopwatch?.Stop();
        _currentAction.CompletedAt = DateTime.UtcNow;
        _currentAction.DurationMs = _actionStopwatch?.ElapsedMilliseconds;
        _currentAction.Result = result.Length > 2000 ? result[..2000] + "... (truncated)" : result;
        _currentAction.Success = success;
        _currentAction.ErrorMessage = errorMessage;
        _currentAction = null;
        _actionStopwatch = null;
    }

    /// <summary>
    /// Record the screenshot captured after actions in this step.
    /// </summary>
    public void RecordScreenshot(string path, long sizeBytes)
    {
        if (_currentStep == null) return;

        _currentStep.Screenshot = new ScreenshotEvidence
        {
            Path = path,
            CapturedAt = DateTime.UtcNow,
            SizeBytes = sizeBytes
        };
        _currentStep.SettleDelayMs = _chain.Config.SettleDelayMs;
    }

    /// <summary>
    /// Record the LLM's observation about the screenshot on the previous step.
    /// Called at the top of the next iteration when we have the LLM response.
    /// </summary>
    public void RecordObservation(string observation)
    {
        // Find the last step that doesn't have an observation yet
        var step = _chain.Steps.LastOrDefault(s => s.Observation == null && s.Screenshot != null);
        if (step == null) return;

        step.Observation = observation;
        step.ObservedAt = DateTime.UtcNow;
        step.StepCompletedAt = DateTime.UtcNow;
        step.StepDurationMs = step.StepCompletedAt.HasValue
            ? (long)(step.StepCompletedAt.Value - step.StepStartedAt).TotalMilliseconds
            : null;
    }

    /// <summary>
    /// Finalize the chain as a success.
    /// </summary>
    public void CompleteSuccess(string finalResponse)
    {
        Finalize("success", finalResponse);
    }

    /// <summary>
    /// Finalize the chain when max iterations reached.
    /// </summary>
    public void CompleteMaxIterations(string finalResponse)
    {
        Finalize("max_iterations", finalResponse);
    }

    /// <summary>
    /// Finalize the chain when cancelled by user.
    /// </summary>
    public void CompleteCancelled()
    {
        Finalize("cancelled", null);
    }

    /// <summary>
    /// Finalize the chain on error.
    /// </summary>
    public void CompleteError(string errorMessage)
    {
        Finalize("error", errorMessage);
    }

    private void Finalize(string outcome, string? finalResponse)
    {
        if (_finalized) return;
        _finalized = true;

        _totalStopwatch.Stop();
        _chain.CompletedAt = DateTime.UtcNow;
        _chain.Outcome = outcome;
        _chain.FinalResponse = finalResponse;
        _chain.TotalDurationMs = _totalStopwatch.ElapsedMilliseconds;

        // Close out any open step
        if (_currentStep != null && _currentStep.StepCompletedAt == null)
        {
            _stepStopwatch?.Stop();
            _currentStep.StepCompletedAt = DateTime.UtcNow;
            _currentStep.StepDurationMs = _stepStopwatch?.ElapsedMilliseconds;
        }

        Save();
    }

    /// <summary>
    /// Write evidence_chain.json to the session folder. Safe to call multiple times for crash recovery.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = Path.Combine(_sessionFolder, "evidence_chain.json");
            var json = JsonSerializer.Serialize(_chain, s_jsonOptions);
            File.WriteAllText(path, json);
            Log.Debug("EvidenceChainLogger: saved to {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "EvidenceChainLogger: failed to save evidence chain");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_finalized)
        {
            Finalize("incomplete", null);
        }
    }
}
