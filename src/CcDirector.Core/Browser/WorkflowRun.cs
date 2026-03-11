using System.Text.Json.Serialization;

namespace CcDirector.Core.Browser;

/// <summary>
/// An immutable record of a single workflow execution with per-step screenshots.
/// </summary>
public class WorkflowRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("workflowName")]
    public string WorkflowName { get; set; } = "";

    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("completedAt")]
    public string? CompletedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "running";

    [JsonPropertyName("parameterValues")]
    public Dictionary<string, string> ParameterValues { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<WorkflowRunStep> Steps { get; set; } = new();
}

/// <summary>
/// A single step within a workflow run, recording what happened and the visual evidence.
/// </summary>
public class WorkflowRunStep
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("params")]
    public Dictionary<string, object>? Params { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("screenshotFile")]
    public string? ScreenshotFile { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
