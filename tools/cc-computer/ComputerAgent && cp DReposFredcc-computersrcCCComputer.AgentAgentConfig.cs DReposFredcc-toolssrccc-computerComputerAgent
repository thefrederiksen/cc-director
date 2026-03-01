using System.Text.Json;

namespace CCComputer.Agent;

/// <summary>
/// Logs all agent activity to persistent storage for later review.
/// Creates a session folder with screenshots and activity log.
/// </summary>
public class ActivityLogger : IDisposable
{
    private readonly string _sessionFolder;
    private readonly string _screenshotsFolder;
    private readonly string _activityLogPath;
    private readonly StreamWriter _logWriter;
    private int _screenshotCounter;
    private bool _disposed;

    public string SessionFolder => _sessionFolder;
    public string ScreenshotsFolder => _screenshotsFolder;

    public ActivityLogger(string? baseFolder = null)
    {
        // Default to AppData/CCComputer/sessions
        baseFolder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CCComputer",
            "sessions");

        // Create session folder with timestamp
        var sessionName = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        _sessionFolder = Path.Combine(baseFolder, sessionName);
        _screenshotsFolder = Path.Combine(_sessionFolder, "screenshots");

        // Ensure folders exist
        Directory.CreateDirectory(_sessionFolder);
        Directory.CreateDirectory(_screenshotsFolder);

        // Create activity log file
        _activityLogPath = Path.Combine(_sessionFolder, "activity.jsonl");
        _logWriter = new StreamWriter(_activityLogPath, append: true) { AutoFlush = true };

        // Log session start
        LogEvent("session_start", new
        {
            sessionFolder = _sessionFolder,
            startTime = DateTime.Now.ToString("o"),
            machineName = Environment.MachineName,
            userName = Environment.UserName
        });
    }

    /// <summary>
    /// Log a user request
    /// </summary>
    public void LogUserRequest(string request)
    {
        LogEvent("user_request", new { content = request });
    }

    /// <summary>
    /// Log agent thinking/reasoning
    /// </summary>
    public void LogThinking(string message)
    {
        LogEvent("thinking", new { content = message });
    }

    /// <summary>
    /// Log a tool call
    /// </summary>
    public void LogToolCall(string toolName, object? arguments = null)
    {
        LogEvent("tool_call", new
        {
            tool = toolName,
            arguments = arguments
        });
    }

    /// <summary>
    /// Log a tool result
    /// </summary>
    public void LogToolResult(string toolName, bool success, string result)
    {
        // Truncate very long results
        var truncatedResult = result.Length > 2000
            ? result.Substring(0, 2000) + "... (truncated)"
            : result;

        LogEvent("tool_result", new
        {
            tool = toolName,
            success = success,
            result = truncatedResult
        });
    }

    /// <summary>
    /// Log and save a screenshot
    /// </summary>
    public string LogScreenshot(string sourcePath, string reason)
    {
        _screenshotCounter++;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var filename = $"{_screenshotCounter:D3}_{timestamp}.png";
        var destPath = Path.Combine(_screenshotsFolder, filename);

        try
        {
            // Copy screenshot to session folder
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            LogEvent("screenshot", new
            {
                path = destPath,
                originalPath = sourcePath,
                reason = reason,
                sequenceNumber = _screenshotCounter
            });

            return destPath;
        }
        catch (Exception ex)
        {
            LogEvent("screenshot_error", new
            {
                originalPath = sourcePath,
                error = ex.Message
            });
            return sourcePath;
        }
    }

    /// <summary>
    /// Log agent response
    /// </summary>
    public void LogResponse(string response)
    {
        LogEvent("agent_response", new { content = response });
    }

    /// <summary>
    /// Log an error
    /// </summary>
    public void LogError(string message, Exception? exception = null)
    {
        LogEvent("error", new
        {
            message = message,
            exceptionType = exception?.GetType().Name,
            exceptionMessage = exception?.Message,
            stackTrace = exception?.StackTrace
        });
    }

    /// <summary>
    /// Log task completion
    /// </summary>
    public void LogTaskComplete(bool success, string summary)
    {
        LogEvent("task_complete", new
        {
            success = success,
            summary = summary
        });
    }

    /// <summary>
    /// Log user stopping the agent
    /// </summary>
    public void LogStopped(string reason)
    {
        LogEvent("stopped", new { reason = reason });
    }

    private void LogEvent(string eventType, object data)
    {
        var entry = new
        {
            timestamp = DateTime.Now.ToString("o"),
            type = eventType,
            data = data
        };

        var json = JsonSerializer.Serialize(entry);
        lock (_logWriter)
        {
            _logWriter.WriteLine(json);
        }
    }

    /// <summary>
    /// Get the path to the activity log file
    /// </summary>
    public string GetActivityLogPath() => _activityLogPath;

    /// <summary>
    /// Get a summary of the session
    /// </summary>
    public SessionSummary GetSummary()
    {
        return new SessionSummary
        {
            SessionFolder = _sessionFolder,
            ActivityLogPath = _activityLogPath,
            ScreenshotCount = _screenshotCounter,
            ScreenshotsFolder = _screenshotsFolder
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            LogEvent("session_end", new { endTime = DateTime.Now.ToString("o") });
            _logWriter.Dispose();
            _disposed = true;
        }
    }
}

public class SessionSummary
{
    public string SessionFolder { get; set; } = "";
    public string ActivityLogPath { get; set; } = "";
    public int ScreenshotCount { get; set; }
    public string ScreenshotsFolder { get; set; } = "";
}
