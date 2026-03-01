namespace CCComputer.App.Models;

/// <summary>
/// Represents a single entry in the agent activity log.
/// </summary>
public class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the formatted timestamp for display in the UI.
    /// </summary>
    public string TimestampFormatted => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// Gets the color for the category badge based on the category type.
    /// </summary>
    public string CategoryColor => Category.ToUpperInvariant() switch
    {
        "START" => "#0078D4",    // Blue
        "THINK" => "#6B5B95",    // Purple
        "TOOL" => "#2D7D46",     // Green
        "RESULT" => "#2D7D46",   // Green
        "DONE" => "#107C10",     // Bright Green
        "ERROR" => "#C42B1C",    // Red
        "STOP" => "#C42B1C",     // Red
        "INFO" => "#0078D4",     // Blue
        _ => "#606060"           // Gray
    };

    public static LogEntry Start(string message) => new()
    {
        Category = "Start",
        Message = message
    };

    public static LogEntry Think(string message) => new()
    {
        Category = "Think",
        Message = message
    };

    public static LogEntry Tool(string toolName, string? args = null) => new()
    {
        Category = "Tool",
        Message = string.IsNullOrEmpty(args) ? toolName : $"{toolName}: {args}"
    };

    public static LogEntry Result(string message) => new()
    {
        Category = "Result",
        Message = message
    };

    public static LogEntry Done(string message) => new()
    {
        Category = "Done",
        Message = message
    };

    public static LogEntry Error(string message) => new()
    {
        Category = "Error",
        Message = message
    };

    public static LogEntry Stopped(string reason) => new()
    {
        Category = "Stop",
        Message = reason
    };

    public static LogEntry Info(string message) => new()
    {
        Category = "Info",
        Message = message
    };
}
