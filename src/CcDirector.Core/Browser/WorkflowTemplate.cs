using System.Text.Json.Serialization;

namespace CcDirector.Core.Browser;

/// <summary>
/// A reusable workflow template with optional parameterization.
/// Enhanced from the original flat workflow JSON format.
/// </summary>
public class WorkflowTemplate
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("parameters")]
    public List<WorkflowParameter> Parameters { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<WorkflowAction> Actions { get; set; } = new();
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
