namespace CCComputer.Agent;

/// <summary>
/// Configuration for the computer control agent.
/// </summary>
public class AgentConfig
{
    /// <summary>
    /// LLM provider (OpenAI, Azure, etc.)
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    /// <summary>
    /// Model ID to use (e.g., gpt-4o, gpt-4-turbo)
    /// </summary>
    public string ModelId { get; set; } = "gpt-4o";

    /// <summary>
    /// API key for the LLM provider
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Path to cc_click executable
    /// </summary>
    public string CcClickPath { get; set; } = string.Empty;

    /// <summary>
    /// User's name for personalization
    /// </summary>
    public string UserName { get; set; } = "User";

    /// <summary>
    /// Delay in milliseconds after tool execution before capturing a screenshot,
    /// allowing screen animations and UI updates to settle.
    /// </summary>
    public int ScreenshotSettleDelayMs { get; set; } = 500;

    /// <summary>
    /// Enable the multi-layered detection pipeline (UIA + OCR + PixelAnalysis).
    /// When enabled, screenshots are annotated with numbered bounding boxes
    /// and the LLM receives a structured element list.
    /// </summary>
    public bool EnableDetectionPipeline { get; set; } = true;

    /// <summary>
    /// Enable OCR text detection (Tier 2) in the detection pipeline.
    /// </summary>
    public bool EnableOcrDetection { get; set; } = true;

    /// <summary>
    /// Enable pixel analysis visual detection (Tier 3) in the detection pipeline.
    /// Uses color segmentation, edge detection, and symbol detection via Python.
    /// </summary>
    public bool EnablePixelAnalysis { get; set; } = true;

    /// <summary>
    /// Path to the Python interpreter (default "python").
    /// </summary>
    public string PythonPath { get; set; } = "python";

    /// <summary>
    /// Path to the pixel_detect.py script. Leave empty to auto-resolve
    /// relative to the application base directory.
    /// </summary>
    public string? PixelDetectScriptPath { get; set; }

    /// <summary>
    /// Validates the configuration
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("API key is required. Set it in appsettings.json under LLM:ApiKey");

        if (string.IsNullOrWhiteSpace(CcClickPath))
            throw new InvalidOperationException("CcClickPath is required. Set it in appsettings.json under Desktop:CcClickPath");

        if (!File.Exists(CcClickPath))
            throw new FileNotFoundException($"cc_click not found at: {CcClickPath}");
    }
}
