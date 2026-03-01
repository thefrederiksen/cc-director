using System.Diagnostics;
using SkiaSharp;
using Serilog;

namespace Trisight.Core.Detection;

/// <summary>
/// Renders annotated screenshots with numbered bounding boxes overlaid on
/// the original screenshot. This is the key visual output that gets sent
/// to the LLM for reasoning (Set-of-Mark / OmniParser style).
/// </summary>
public static class AnnotatedScreenshotRenderer
{
    // Colors for bounding boxes — cycle through for visual distinction
    private static readonly SKColor[] BoxColors =
    [
        new(255, 0, 0),       // Red
        new(0, 150, 0),       // Green
        new(0, 0, 255),       // Blue
        new(255, 165, 0),     // Orange
        new(128, 0, 128),     // Purple
        new(0, 128, 128),     // Teal
        new(255, 20, 147),    // DeepPink
        new(0, 100, 0),       // DarkGreen
        new(70, 130, 180),    // SteelBlue
        new(184, 134, 11),    // DarkGoldenrod
    ];

    /// <summary>
    /// Render an annotated screenshot with numbered bounding boxes.
    /// </summary>
    /// <param name="screenshotPath">Path to the original screenshot PNG.</param>
    /// <param name="elements">Detected elements to annotate.</param>
    /// <param name="outputPath">Path to save the annotated PNG. If null, uses a temp file.</param>
    /// <returns>Path to the annotated screenshot, and its bytes.</returns>
    public static (string Path, byte[] Bytes) Render(
        string screenshotPath,
        List<DetectedElement> elements,
        string? outputPath = null)
    {
        var sw = Stopwatch.StartNew();

        using var inputStream = File.OpenRead(screenshotPath);
        using var bitmap = SKBitmap.Decode(inputStream);
        if (bitmap == null)
            throw new InvalidOperationException($"Failed to decode image: {screenshotPath}");

        var bytes = RenderOnBitmap(bitmap, elements);
        outputPath ??= Path.Combine(Path.GetTempPath(),
            $"annotated_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        File.WriteAllBytes(outputPath, bytes);

        sw.Stop();
        Log.Debug("AnnotatedScreenshotRenderer: Rendered {Count} annotations in {ElapsedMs}ms -> {Path}",
            elements.Count, sw.ElapsedMilliseconds, outputPath);

        return (outputPath, bytes);
    }

    /// <summary>
    /// Render annotations from raw screenshot bytes.
    /// </summary>
    public static byte[] RenderFromBytes(byte[] screenshotBytes, List<DetectedElement> elements)
    {
        using var bitmap = SKBitmap.Decode(screenshotBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode screenshot bytes");

        return RenderOnBitmap(bitmap, elements);
    }

    /// <summary>
    /// Draw bounding boxes and labels on a bitmap.
    /// </summary>
    private static byte[] RenderOnBitmap(SKBitmap bitmap, List<DetectedElement> elements)
    {
        using var canvas = new SKCanvas(bitmap);

        // Prepare paints
        using var boxPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true,
        };

        using var labelBgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        using var labelTextPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true,
        };

        using var labelFont = new SKFont(
            SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12);

        foreach (var elem in elements)
        {
            var color = BoxColors[(elem.Id - 1) % BoxColors.Length];
            boxPaint.Color = color;
            labelBgPaint.Color = color;

            var bounds = elem.Bounds;

            // Draw bounding box
            var rect = new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            canvas.DrawRect(rect, boxPaint);

            // Draw label background + text in top-left corner of the box
            var labelText = elem.Id.ToString();
            float labelW = labelFont.MeasureText(labelText) + 6;
            float labelH = labelFont.Size + 4;

            // Position label just inside the top-left of the bounding box
            float labelX = bounds.Left;
            float labelY = bounds.Top;

            // If near top edge, put label below the top edge instead of above
            if (labelY < labelH)
                labelY = bounds.Top;

            var labelRect = new SKRect(labelX, labelY, labelX + labelW, labelY + labelH);
            canvas.DrawRect(labelRect, labelBgPaint);

            // Draw label text
            canvas.DrawText(labelText,
                labelX + 3,
                labelY + labelH - 3,
                SKTextAlign.Left,
                labelFont,
                labelTextPaint);
        }

        // Encode to PNG
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Generate a compact text summary of all detected elements for the LLM prompt.
    /// </summary>
    public static string GenerateElementSummary(List<DetectedElement> elements)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Detected {elements.Count} UI elements:");
        sb.AppendLine();

        foreach (var elem in elements)
        {
            var parts = new List<string>
            {
                $"[{elem.Id}] {elem.Type}"
            };

            if (!string.IsNullOrWhiteSpace(elem.Name))
                parts.Add($"\"{elem.Name}\"");

            parts.Add($"at ({elem.Bounds.CenterX},{elem.Bounds.CenterY})");

            if (elem.IsInteractable)
                parts.Add("(clickable)");

            if (!elem.IsEnabled)
                parts.Add("(disabled)");

            if (!string.IsNullOrEmpty(elem.State))
                parts.Add($"[{elem.State}]");

            sb.AppendLine(string.Join(" ", parts));
        }

        return sb.ToString();
    }
}
