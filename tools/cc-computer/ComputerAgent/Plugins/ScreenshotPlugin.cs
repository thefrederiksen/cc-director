using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace CCComputer.Agent.Plugins;

/// <summary>
/// Plugin for capturing screenshots using cc_click.
/// Integrates with ActivityLogger to save screenshots to session folder.
/// </summary>
public class ScreenshotPlugin
{
    private readonly string _ccClickPath;
    private readonly string _screenshotFolder;
    private readonly ActivityLogger? _sessionLogger;

    public ScreenshotPlugin(string ccClickPath, ActivityLogger? sessionLogger = null, string? screenshotFolder = null)
    {
        _ccClickPath = ccClickPath;
        _sessionLogger = sessionLogger;

        // Use session's screenshot folder if logger provided, otherwise temp folder
        _screenshotFolder = sessionLogger?.ScreenshotsFolder
            ?? screenshotFolder
            ?? Path.Combine(Path.GetTempPath(), "CCComputer", "Screenshots");

        if (!File.Exists(_ccClickPath))
        {
            throw new FileNotFoundException($"cc_click not found at: {_ccClickPath}");
        }

        // Ensure screenshot folder exists
        Directory.CreateDirectory(_screenshotFolder);
    }

    [KernelFunction("take_screenshot")]
    [Description("Take a screenshot of the entire desktop or a specific window. Returns the file path of the saved screenshot. ALWAYS take a screenshot after important actions to verify success.")]
    public async Task<string> TakeScreenshotAsync(
        [Description("Optional: Window title to capture. If not specified, captures the entire desktop.")] string? windowTitle = null,
        [Description("Optional: Reason for taking screenshot (e.g., 'verify menu opened', 'confirm save dialog')")] string? reason = null)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var filename = $"screenshot_{timestamp}.png";
        var outputPath = Path.Combine(_screenshotFolder, filename);

        var args = $"screenshot -o \"{outputPath}\"";

        if (!string.IsNullOrEmpty(windowTitle))
        {
            args += $" -w \"{windowTitle}\"";
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ccClickPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return $"Error taking screenshot: {error}".Trim();
        }

        if (File.Exists(outputPath))
        {
            var fileInfo = new FileInfo(outputPath);
            var sizeKb = fileInfo.Length / 1024;

            // Log to session if logger is available
            if (_sessionLogger != null)
            {
                var sessionPath = _sessionLogger.LogScreenshot(outputPath, reason ?? "Agent capture");
                return $"Screenshot saved to session: {sessionPath} ({sizeKb} KB)";
            }

            return $"Screenshot saved: {outputPath} ({sizeKb} KB)";
        }

        return $"Screenshot command completed but file not found at {outputPath}";
    }

    /// <summary>
    /// Gets the path to a screenshot file for the LLM to analyze.
    /// This is used when we need to send the actual image to a vision model.
    /// </summary>
    [KernelFunction("get_screenshot_path")]
    [Description("Get the full path to the most recent screenshot file.")]
    public string GetLatestScreenshotPath()
    {
        var screenshots = Directory.GetFiles(_screenshotFolder, "screenshot_*.png")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        return screenshots ?? "No screenshots found";
    }

    /// <summary>
    /// Gets the screenshot folder path.
    /// </summary>
    public string ScreenshotFolder => _screenshotFolder;
}
