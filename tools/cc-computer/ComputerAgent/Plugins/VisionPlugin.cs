using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;
using Serilog;
using Trisight.Core.Detection;
using CCComputer.Agent.Services;

namespace CCComputer.Agent.Plugins;

/// <summary>
/// Plugin for vision-based UI element detection and clicking.
/// Use this when UI Automation (list_elements) fails on modern Windows apps.
///
/// Fallback chain:
///   Step 2a: find_by_text — OCR text search (free, fast, ~95% for text elements)
///   Step 2b: vision_find_element with crop region — region-of-interest vision
///   Step 2c: vision_find_element full-screen — last resort
/// </summary>
public class VisionPlugin : IDisposable
{
    private readonly GptVisionService _visionService;
    private readonly OcrTextDetector _ocrDetector;
    private readonly string _ccClickPath;
    private readonly string _screenshotFolder;
    private bool _disposed;

    public VisionPlugin(string apiKey, string ccClickPath, string? screenshotFolder = null)
    {
        _visionService = new GptVisionService(apiKey);
        _ocrDetector = new OcrTextDetector();
        _ccClickPath = ccClickPath;
        _screenshotFolder = screenshotFolder ?? Path.Combine(Path.GetTempPath(), "CCComputer", "Vision");

        Directory.CreateDirectory(_screenshotFolder);
    }

    [KernelFunction("find_by_text")]
    [Description(@"Find a UI element by its visible text using OCR. This is FAST (local, no API call) and accurate for text-based elements.
Returns coordinates that can be used with click(coordinates: 'x,y').

Try this FIRST before vision_find_element — it's free and instant.
If OCR can't find the text, falls back to vision_find_element automatically.

Use this when you need to click a button, menu item, or label with visible text.")]
    public async Task<string> FindByTextAsync(
        [Description("Text to find on screen, e.g., 'Save', 'File', 'New Email'. Supports partial/fuzzy matching.")] string text,
        [Description("Optional: Window title to search in. If not specified, uses current desktop.")] string? windowTitle = null)
    {
        Log.Debug("VisionPlugin.FindByTextAsync: text={Text}, window={Window}", text, windowTitle);

        try
        {
            // Take a screenshot
            var screenshotPath = await CaptureScreenshotAsync("findtext", windowTitle);
            if (screenshotPath == null)
                return "ERROR: Failed to capture screenshot for OCR text search";

            // Run OCR on the screenshot
            var ocrRegions = await _ocrDetector.DetectTextAsync(screenshotPath);

            if (ocrRegions.Count == 0)
            {
                Log.Warning("VisionPlugin.FindByTextAsync: OCR found no text, falling back to vision");
                return await FindElementAsync(text, windowTitle);
            }

            // Search OCR results for fuzzy text match
            var match = FindBestOcrMatch(text, ocrRegions);

            if (match != null)
            {
                var coords = $"{match.Bounds.CenterX},{match.Bounds.CenterY}";
                Log.Information("VisionPlugin.FindByTextAsync: Found '{Text}' via OCR at {Coords} (matched: '{Matched}', score: {Score:F2})",
                    text, coords, match.Text, match.MatchScore);
                return $"Found '{text}' at coordinates: {coords}. (OCR match: '{match.Text}', confidence: {match.MatchScore:F2}). Use click(coordinates: \"{coords}\") to click it.";
            }

            // OCR didn't find it — fall back to vision_find_element
            Log.Debug("VisionPlugin.FindByTextAsync: OCR didn't match '{Text}', falling back to vision", text);
            return await FindElementAsync(text, windowTitle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VisionPlugin.FindByTextAsync failed: text={Text}", text);
            return $"ERROR: Text search failed: {ex.Message}";
        }
    }

    [KernelFunction("vision_find_element")]
    [Description(@"Use AI vision to find a UI element when list_elements and find_by_text fail.
Takes a screenshot and uses GPT-4 Vision to locate the element.
Returns coordinates that can be used with click(coordinates: 'x,y').

Use this when:
- list_elements returns 'AutomationId property not supported'
- find_by_text can't find the element (non-text elements like icons)
- Modern UWP/WinUI apps don't expose UI elements properly
- You need to click something visible but not in the UI tree

TIP: Pass cropX/cropY/cropWidth/cropHeight to focus on a region (e.g., toolbar area).
This greatly improves accuracy by giving the vision model a smaller area to reason about.")]
    public async Task<string> FindElementAsync(
        [Description("What to find, e.g., 'Save As menu item', 'New Email button', 'Send button'")] string target,
        [Description("Optional: Window title to search in. If not specified, uses current desktop.")] string? windowTitle = null,
        [Description("Optional: Left edge of crop region in pixels. Use with cropY/cropWidth/cropHeight to focus vision on a specific area.")] int? cropX = null,
        [Description("Optional: Top edge of crop region in pixels.")] int? cropY = null,
        [Description("Optional: Width of crop region in pixels.")] int? cropWidth = null,
        [Description("Optional: Height of crop region in pixels.")] int? cropHeight = null)
    {
        Log.Debug("VisionPlugin.FindElementAsync: target={Target}, window={Window}, crop=({CX},{CY},{CW},{CH})",
            target, windowTitle, cropX, cropY, cropWidth, cropHeight);

        try
        {
            // Take a screenshot first
            var screenshotPath = await CaptureScreenshotAsync("vision", windowTitle);
            if (screenshotPath == null)
                return "ERROR: Failed to capture screenshot for vision analysis";

            // Build crop region if all crop parameters are provided
            (int X, int Y, int Width, int Height)? cropRegion = null;
            if (cropX.HasValue && cropY.HasValue && cropWidth.HasValue && cropHeight.HasValue)
            {
                cropRegion = (cropX.Value, cropY.Value, cropWidth.Value, cropHeight.Value);
            }

            // Use vision to find the element
            var coordinates = await _visionService.FindElementCoordinatesAsync(screenshotPath, target, cropRegion);

            if (coordinates.StartsWith("NOT_FOUND"))
            {
                Log.Warning("VisionPlugin: Element not found: {Target}", target);
                return coordinates;
            }

            if (coordinates.StartsWith("PARSE_ERROR"))
            {
                Log.Warning("VisionPlugin: Parse error: {Result}", coordinates);
                return coordinates;
            }

            Log.Information("VisionPlugin: Found '{Target}' at coordinates {Coords}", target, coordinates);
            return $"Found '{target}' at coordinates: {coordinates}. Use click(coordinates: \"{coordinates}\") to click it.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VisionPlugin.FindElementAsync failed: target={Target}", target);
            return $"ERROR: Vision search failed: {ex.Message}";
        }
    }

    [KernelFunction("vision_click")]
    [Description(@"Find a UI element and click it in one step.
Tries OCR text search first (fast), then falls back to vision AI.
Use this when list_elements fails and you need to click something.")]
    public async Task<string> VisionClickAsync(
        [Description("What to click, e.g., 'Save As menu item', 'New Email button'")] string target,
        [Description("Optional: Window title to search in")] string? windowTitle = null)
    {
        Log.Debug("VisionPlugin.VisionClickAsync: target={Target}, window={Window}", target, windowTitle);

        // Try find_by_text first (uses OCR, falls back to vision internally)
        var findResult = await FindByTextAsync(target, windowTitle);

        if (findResult.StartsWith("NOT_FOUND") || findResult.StartsWith("ERROR") || findResult.StartsWith("PARSE_ERROR"))
        {
            return findResult;
        }

        // Extract coordinates from result
        // Format: "Found 'target' at coordinates: x,y. ..."
        var coordsMatch = System.Text.RegularExpressions.Regex.Match(findResult, @"coordinates: (\d+),(\d+)");
        if (!coordsMatch.Success)
        {
            return $"ERROR: Could not parse coordinates from: {findResult}";
        }

        var x = coordsMatch.Groups[1].Value;
        var y = coordsMatch.Groups[2].Value;
        var coords = $"{x},{y}";

        // Now click at those coordinates
        try
        {
            var args = $"click --xy \"{coords}\"";

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
                return $"ERROR: Click at {coords} failed: {error}".Trim();
            }

            Log.Information("VisionPlugin: Clicked '{Target}' at {Coords}", target, coords);
            return $"Clicked '{target}' at coordinates {coords}. Take a screenshot to verify the action succeeded.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VisionPlugin.VisionClickAsync click failed: coords={Coords}", coords);
            return $"ERROR: Click failed: {ex.Message}";
        }
    }

    [KernelFunction("vision_describe")]
    [Description(@"Use AI vision to describe what's visible in the current screen or window.
Useful for understanding the current state when UI automation is failing.")]
    public async Task<string> DescribeScreenAsync(
        [Description("Optional: Window title to describe. If not specified, describes entire desktop.")] string? windowTitle = null)
    {
        Log.Debug("VisionPlugin.DescribeScreenAsync: window={Window}", windowTitle);

        try
        {
            var screenshotPath = await CaptureScreenshotAsync("describe", windowTitle);
            if (screenshotPath == null)
                return "ERROR: Failed to capture screenshot";

            var description = await _visionService.DescribeScreenshotAsync(screenshotPath);

            Log.Debug("VisionPlugin.DescribeScreenAsync result: {Desc}", description.Length > 200 ? description[..200] + "..." : description);
            return description;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VisionPlugin.DescribeScreenAsync failed");
            return $"ERROR: Vision describe failed: {ex.Message}";
        }
    }

    [KernelFunction("vision_verify")]
    [Description(@"Use AI vision to verify a condition is true.
Use this to verify actions succeeded, e.g., 'is the Save As dialog open?'")]
    public async Task<string> VerifyConditionAsync(
        [Description("The condition to verify, e.g., 'is the file save dialog open?', 'is the email compose window visible?'")] string condition,
        [Description("Optional: Window title to check")] string? windowTitle = null)
    {
        Log.Debug("VisionPlugin.VerifyConditionAsync: condition={Condition}, window={Window}", condition, windowTitle);

        try
        {
            var screenshotPath = await CaptureScreenshotAsync("verify", windowTitle);
            if (screenshotPath == null)
                return "ERROR: Failed to capture screenshot";

            var result = await _visionService.VerifyConditionAsync(screenshotPath, condition);

            Log.Debug("VisionPlugin.VerifyConditionAsync result: {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VisionPlugin.VerifyConditionAsync failed");
            return $"ERROR: Vision verify failed: {ex.Message}";
        }
    }

    #region Private helpers

    /// <summary>
    /// Capture a screenshot via cc_click and return the path, or null on failure.
    /// </summary>
    private async Task<string?> CaptureScreenshotAsync(string prefix, string? windowTitle)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var screenshotPath = Path.Combine(_screenshotFolder, $"{prefix}_{timestamp}.png");

        var args = $"screenshot -o \"{screenshotPath}\"";
        if (!string.IsNullOrEmpty(windowTitle))
            args += $" -w \"{windowTitle}\"";

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
        await process.WaitForExitAsync();

        return File.Exists(screenshotPath) ? screenshotPath : null;
    }

    /// <summary>
    /// Result of an OCR fuzzy text match, including the match quality score.
    /// </summary>
    private sealed class OcrMatchResult
    {
        public required string Text { get; init; }
        public required BoundingRect Bounds { get; init; }
        public required double MatchScore { get; init; }
    }

    /// <summary>
    /// Search OCR text regions for the best fuzzy match to the target text.
    /// Returns null if no match meets the minimum threshold.
    /// </summary>
    private static OcrMatchResult? FindBestOcrMatch(string target, List<TextRegion> ocrRegions)
    {
        const double MinMatchScore = 0.5;

        var targetLower = target.Trim().ToLowerInvariant();
        OcrMatchResult? best = null;

        foreach (var region in ocrRegions)
        {
            var regionLower = region.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(regionLower)) continue;

            double score = ComputeTextMatchScore(targetLower, regionLower);

            if (score >= MinMatchScore && (best == null || score > best.MatchScore))
            {
                best = new OcrMatchResult
                {
                    Text = region.Text,
                    Bounds = region.Bounds,
                    MatchScore = score,
                };
            }
        }

        return best;
    }

    /// <summary>
    /// Compute a fuzzy match score between target and candidate text (0.0 to 1.0).
    /// Handles exact match, containment, and character-level similarity.
    /// </summary>
    private static double ComputeTextMatchScore(string target, string candidate)
    {
        // Exact match
        if (target == candidate) return 1.0;

        // One contains the other
        if (candidate.Contains(target)) return 0.9;
        if (target.Contains(candidate)) return 0.85;

        // Case-insensitive word-level: target words all appear in candidate
        var targetWords = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (targetWords.Length > 0)
        {
            int matchedWords = targetWords.Count(tw =>
                candidateWords.Any(cw => cw.Contains(tw) || tw.Contains(cw)));

            if (matchedWords == targetWords.Length)
                return 0.8;
        }

        // Character overlap (Jaccard-like)
        var targetChars = new HashSet<char>(target);
        var candidateChars = new HashSet<char>(candidate);
        int intersection = targetChars.Intersect(candidateChars).Count();
        int union = targetChars.Union(candidateChars).Count();

        return union > 0 ? (double)intersection / union * 0.6 : 0;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _visionService.Dispose();
            _disposed = true;
        }
    }
}
