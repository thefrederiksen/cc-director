using System.Text.Json.Serialization;

namespace CcDirector.Core.Browser;

/// <summary>
/// A reusable workflow template with optional parameterization.
/// Version 1: flat actions list.
/// Version 2: steps list with condition support.
/// </summary>
public class WorkflowTemplate
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("initialScreenshotFile")]
    public string? InitialScreenshotFile { get; set; }

    [JsonPropertyName("parameters")]
    public List<WorkflowParameter> Parameters { get; set; } = new();

    /// <summary>
    /// V1 format: flat action list. Kept for backward compat on load.
    /// On save, always write v2 steps instead.
    /// </summary>
    [JsonPropertyName("actions")]
    public List<WorkflowAction> Actions { get; set; } = new();

    /// <summary>
    /// V2 format: structured step list with condition support.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<WorkflowStep> Steps { get; set; } = new();
}

/// <summary>
/// A named variable that can be substituted into action param values at run time.
/// </summary>
public class WorkflowParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("defaultValue")]
    public string DefaultValue { get; set; } = "";
}

/// <summary>
/// A single recorded browser action (click, type, navigate, etc.).
/// </summary>
public class WorkflowAction
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("params")]
    public Dictionary<string, object>? Params { get; set; }

    [JsonPropertyName("screenshotFile")]
    public string? ScreenshotFile { get; set; }
}

/// <summary>
/// A wrapper that is either an action or a conditional branch.
/// </summary>
public class WorkflowStep
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "action";

    [JsonPropertyName("action")]
    public WorkflowAction? Action { get; set; }

    [JsonPropertyName("condition")]
    public WorkflowCondition? Condition { get; set; }
}

/// <summary>
/// A conditional branch: evaluate a check, then run thenSteps or elseSteps.
/// </summary>
public class WorkflowCondition
{
    /// <summary>Check type: elementExists, urlContains, textVisible.</summary>
    [JsonPropertyName("check")]
    public string Check { get; set; } = "elementExists";

    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("thenSteps")]
    public List<WorkflowStep> ThenSteps { get; set; } = new();

    [JsonPropertyName("elseSteps")]
    public List<WorkflowStep> ElseSteps { get; set; } = new();
}
