using System.Diagnostics;
using Serilog;

namespace Trisight.Core.Detection;

/// <summary>
/// Orchestrates the multi-tiered detection pipeline:
///   Tier 1: UIA (pixel-perfect coordinates from accessibility tree)
///   Tier 2: OCR (text detection for custom-drawn controls)
///   Tier 3: PixelAnalysis (visual detection for non-text elements)
///   Fusion: Merge all sources into unified element map
///   Annotation: Overlay numbered bounding boxes on screenshot
/// </summary>
public class DetectionPipeline : IDisposable
{
    private readonly UiaElementDetector _uiaDetector;
    private readonly OcrTextDetector _ocrDetector;
    private readonly PixelAnalysisDetector _pixelDetector;
    private readonly ElementFusionEngine _fusionEngine;
    private bool _disposed;

    /// <summary>
    /// Whether to run OCR detection (Tier 2).
    /// </summary>
    public bool EnableOcr { get; set; } = true;

    /// <summary>
    /// Whether to run PixelAnalysis detection (Tier 3).
    /// </summary>
    public bool EnablePixelAnalysis { get; set; } = true;

    /// <summary>
    /// Maximum UIA tree depth to walk.
    /// </summary>
    public int MaxUiaDepth { get; set; } = 15;

    public DetectionPipeline(string pythonPath = "python", string? pixelDetectScriptPath = null)
    {
        _uiaDetector = new UiaElementDetector();
        _ocrDetector = new OcrTextDetector();
        _pixelDetector = new PixelAnalysisDetector(pythonPath, pixelDetectScriptPath);
        _fusionEngine = new ElementFusionEngine();
    }

    /// <summary>
    /// Run the full detection pipeline for a window.
    /// </summary>
    /// <param name="windowTitle">Title of the target window.</param>
    /// <param name="screenshotPath">Path to screenshot for OCR/PixelAnalysis/annotation.</param>
    /// <returns>Detection result with fused elements and optional annotated screenshot.</returns>
    public async Task<DetectionResult> DetectAsync(string windowTitle, string screenshotPath)
    {
        var sw = Stopwatch.StartNew();
        var result = new DetectionResult { WindowTitle = windowTitle };

        // Find the window handle for UIA
        var windowHandle = _uiaDetector.FindWindowHandle(windowTitle);
        result.WindowHandle = windowHandle;

        // Tier 1: UIA detection
        var uiaElements = new List<DetectedElement>();
        if (windowHandle != IntPtr.Zero)
        {
            uiaElements = _uiaDetector.DetectElements(windowHandle, MaxUiaDepth);
            result.UiaElementCount = uiaElements.Count;
            Log.Debug("DetectionPipeline: Tier 1 (UIA) found {Count} elements", uiaElements.Count);
        }
        else
        {
            Log.Warning("DetectionPipeline: Window '{Title}' not found for UIA — relying on OCR/PixelAnalysis", windowTitle);
        }

        // Tier 2 & 3: Run OCR and PixelAnalysis in parallel
        var ocrRegions = new List<TextRegion>();
        var pixelDetections = new List<VisualElement>();

        if (File.Exists(screenshotPath))
        {
            var tasks = new List<Task>();

            if (EnableOcr)
            {
                tasks.Add(Task.Run(async () =>
                {
                    ocrRegions = await _ocrDetector.DetectTextAsync(screenshotPath);
                    result.OcrRegionCount = ocrRegions.Count;
                    Log.Debug("DetectionPipeline: Tier 2 (OCR) found {Count} text regions", ocrRegions.Count);
                }));
            }

            if (EnablePixelAnalysis && _pixelDetector.IsAvailable)
            {
                tasks.Add(Task.Run(async () =>
                {
                    pixelDetections = await _pixelDetector.DetectElementsAsync(screenshotPath);
                    result.PixelAnalysisCount = pixelDetections.Count;
                    Log.Debug("DetectionPipeline: Tier 3 (PixelAnalysis) found {Count} detections", pixelDetections.Count);
                }));
            }

            await Task.WhenAll(tasks);
        }
        else
        {
            Log.Warning("DetectionPipeline: Screenshot not found at {Path} — UIA-only mode", screenshotPath);
        }

        // Fusion: merge all sources
        result.Elements = _fusionEngine.Fuse(uiaElements, ocrRegions, pixelDetections);

        sw.Stop();
        result.DetectionTimeMs = sw.ElapsedMilliseconds;

        Log.Information("DetectionPipeline: Completed in {ElapsedMs}ms — " +
            "{Total} fused elements (UIA={Uia}, OCR={Ocr}, PixelAnalysis={Pixel})",
            sw.ElapsedMilliseconds, result.Elements.Count,
            result.UiaElementCount, result.OcrRegionCount, result.PixelAnalysisCount);

        return result;
    }

    /// <summary>
    /// Run detection and produce an annotated screenshot with element summary.
    /// This is the primary method used by the agent loop.
    /// </summary>
    /// <param name="windowTitle">Title of the target window.</param>
    /// <param name="screenshotPath">Path to the raw screenshot.</param>
    /// <param name="annotatedOutputPath">Where to save the annotated screenshot.</param>
    /// <returns>Detection result with annotated screenshot path and element list.</returns>
    public async Task<DetectionResult> DetectAndAnnotateAsync(
        string windowTitle,
        string screenshotPath,
        string? annotatedOutputPath = null)
    {
        var result = await DetectAsync(windowTitle, screenshotPath);

        // Generate annotated screenshot if we have elements and a screenshot
        if (result.Elements.Count > 0 && File.Exists(screenshotPath))
        {
            try
            {
                annotatedOutputPath ??= Path.Combine(
                    Path.GetDirectoryName(screenshotPath) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(screenshotPath) + "_annotated.png");

                var (path, _) = AnnotatedScreenshotRenderer.Render(
                    screenshotPath, result.Elements, annotatedOutputPath);
                result.AnnotatedScreenshotPath = path;

                Log.Debug("DetectionPipeline: Annotated screenshot saved to {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DetectionPipeline: Failed to render annotated screenshot");
            }
        }

        return result;
    }

    /// <summary>
    /// Run detection from raw screenshot bytes (no file required).
    /// </summary>
    public async Task<(List<DetectedElement> Elements, byte[]? AnnotatedBytes)> DetectFromBytesAsync(
        string windowTitle,
        byte[] screenshotBytes)
    {
        var windowHandle = _uiaDetector.FindWindowHandle(windowTitle);

        // Tier 1: UIA
        var uiaElements = windowHandle != IntPtr.Zero
            ? _uiaDetector.DetectElements(windowHandle, MaxUiaDepth)
            : new List<DetectedElement>();

        // Tier 2: OCR
        var ocrRegions = EnableOcr
            ? await _ocrDetector.DetectTextAsync(screenshotBytes)
            : new List<TextRegion>();

        // Tier 3: PixelAnalysis
        var pixelDetections = EnablePixelAnalysis && _pixelDetector.IsAvailable
            ? await _pixelDetector.DetectElementsAsync(screenshotBytes)
            : new List<VisualElement>();

        // Fusion
        var elements = _fusionEngine.Fuse(uiaElements, ocrRegions, pixelDetections);

        // Annotate
        byte[]? annotatedBytes = null;
        if (elements.Count > 0)
        {
            try
            {
                annotatedBytes = AnnotatedScreenshotRenderer.RenderFromBytes(screenshotBytes, elements);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DetectionPipeline: Failed to render annotations from bytes");
            }
        }

        return (elements, annotatedBytes);
    }

    /// <summary>
    /// Look up an element by its ID from a previous detection result.
    /// </summary>
    public static DetectedElement? FindElementById(List<DetectedElement> elements, int elementId)
    {
        return elements.FirstOrDefault(e => e.Id == elementId);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _uiaDetector.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
