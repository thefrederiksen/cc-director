using System.Text.Json.Serialization;

namespace CCComputer.Agent;

/// <summary>
/// Root evidence chain for a single user request — every action paired with its screenshot and observation.
/// </summary>
public class EvidenceChain
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("request")]
    public string Request { get; set; } = "";

    [JsonPropertyName("config")]
    public EvidenceConfig Config { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<EvidenceStep> Steps { get; set; } = [];

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("finalResponse")]
    public string? FinalResponse { get; set; }

    [JsonPropertyName("totalDurationMs")]
    public long? TotalDurationMs { get; set; }
}

/// <summary>
/// Configuration snapshot recorded in the evidence chain.
/// </summary>
public class EvidenceConfig
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("settleDelayMs")]
    public int SettleDelayMs { get; set; }
}

/// <summary>
/// One step in the evidence chain: actions taken → screenshot captured → LLM observation.
/// </summary>
public class EvidenceStep
{
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; set; }

    [JsonPropertyName("stepStartedAt")]
    public DateTime StepStartedAt { get; set; }

    [JsonPropertyName("stepCompletedAt")]
    public DateTime? StepCompletedAt { get; set; }

    [JsonPropertyName("stepDurationMs")]
    public long? StepDurationMs { get; set; }

    [JsonPropertyName("actions")]
    public List<ToolAction> Actions { get; set; } = [];

    [JsonPropertyName("settleDelayMs")]
    public int SettleDelayMs { get; set; }

    [JsonPropertyName("screenshot")]
    public ScreenshotEvidence? Screenshot { get; set; }

    [JsonPropertyName("observation")]
    public string? Observation { get; set; }

    [JsonPropertyName("observedAt")]
    public DateTime? ObservedAt { get; set; }
}

/// <summary>
/// A single tool invocation with timing and result data.
/// </summary>
public class ToolAction
{
    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("arguments")]
    public Dictionary<string, object?> Arguments { get; set; } = [];

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Screenshot metadata in the evidence chain.
/// </summary>
public class ScreenshotEvidence
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("capturedAt")]
    public DateTime CapturedAt { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}
