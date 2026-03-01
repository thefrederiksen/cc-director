using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace Trisight.Core.Detection;

/// <summary>
/// Detects UI elements using pixel-based analysis (color segmentation, edge
/// detection, symbol detection) via a Python subprocess.
/// This is Tier 3 — catches non-text custom-drawn controls that UIA and OCR miss.
/// </summary>
public class PixelAnalysisDetector
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly bool _isAvailable;

    public PixelAnalysisDetector(string pythonPath = "python", string? scriptPath = null)
    {
        _pythonPath = pythonPath;

        // Auto-resolve script path relative to app base directory
        _scriptPath = scriptPath ?? "";
        if (string.IsNullOrEmpty(_scriptPath))
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            // Walk up from bin/Debug/net*/  to repo root, then into tools/
            var candidate = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "tools", "pixel_detect.py"));
            if (File.Exists(candidate))
            {
                _scriptPath = candidate;
            }
        }

        _isAvailable = !string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath);

        if (_isAvailable)
        {
            Log.Information("PixelAnalysisDetector: Script found at {Path}", _scriptPath);
        }
        else
        {
            Log.Debug("PixelAnalysisDetector: Script not found — Tier 3 detection disabled. " +
                       "Looked for: {Path}", _scriptPath);
        }
    }

    /// <summary>
    /// Whether the pixel analysis detector is ready.
    /// </summary>
    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Detect visual UI elements in a screenshot.
    /// </summary>
    public async Task<List<VisualElement>> DetectElementsAsync(string screenshotPath, double confidenceThreshold = 0.3)
    {
        if (!_isAvailable)
        {
            return new List<VisualElement>();
        }

        return await RunAnalysisAsync(screenshotPath, confidenceThreshold);
    }

    /// <summary>
    /// Detect visual UI elements from raw image bytes.
    /// </summary>
    public async Task<List<VisualElement>> DetectElementsAsync(byte[] imageBytes, double confidenceThreshold = 0.3)
    {
        if (!_isAvailable)
        {
            return new List<VisualElement>();
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"pixel_input_{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempPath, imageBytes);
            return await RunAnalysisAsync(tempPath, confidenceThreshold);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private async Task<List<VisualElement>> RunAnalysisAsync(string screenshotPath, double confidenceThreshold)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<VisualElement>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{_scriptPath}\" \"{screenshotPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Log.Warning("PixelAnalysisDetector: Python script failed (exit {Code}): {Error}",
                    process.ExitCode, stderr.Trim());
                return results;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                Log.Warning("PixelAnalysisDetector: No output from Python script");
                return results;
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (!root.TryGetProperty("elements", out var elementsArray))
            {
                return results;
            }

            foreach (var elem in elementsArray.EnumerateArray())
            {
                var bbox = elem.GetProperty("bbox");
                var x1 = bbox[0].GetInt32();
                var y1 = bbox[1].GetInt32();
                var x2 = bbox[2].GetInt32();
                var y2 = bbox[3].GetInt32();

                var confidence = elem.TryGetProperty("confidence", out var confProp)
                    ? confProp.GetDouble()
                    : 0.8;

                if (confidence < confidenceThreshold)
                    continue;

                var type = elem.TryGetProperty("type", out var typeProp)
                    ? typeProp.GetString() ?? "button"
                    : "button";

                results.Add(new VisualElement
                {
                    Type = char.ToUpper(type[0]) + type[1..],
                    Bounds = new BoundingRect(x1, y1, x2 - x1, y2 - y1),
                    Confidence = confidence,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PixelAnalysisDetector: Failed to run pixel analysis");
        }

        sw.Stop();
        Log.Debug("PixelAnalysisDetector: Completed in {ElapsedMs}ms, found {Count} elements",
            sw.ElapsedMilliseconds, results.Count);

        return results;
    }
}
