using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Serilog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Trisight.Core.Detection;

/// <summary>
/// Detects text regions in screenshots using the built-in Windows.Media.Ocr API.
/// This is Tier 2 — complements UIA by finding text in custom-drawn controls
/// that UIA cannot see.
/// </summary>
public class OcrTextDetector
{
    private readonly OcrEngine _engine;

    public OcrTextDetector()
    {
        // Use English OCR engine (or system default)
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No OCR engine available. Ensure Windows language packs are installed.");
    }

    /// <summary>
    /// Detect text regions in a screenshot file.
    /// </summary>
    /// <param name="screenshotPath">Path to the PNG screenshot.</param>
    /// <returns>List of detected text regions with bounding boxes.</returns>
    public async Task<List<TextRegion>> DetectTextAsync(string screenshotPath)
    {
        var sw = Stopwatch.StartNew();
        var regions = new List<TextRegion>();

        try
        {
            // Load the image as a SoftwareBitmap for Windows OCR
            using var bitmap = await LoadSoftwareBitmapAsync(screenshotPath);
            if (bitmap == null)
            {
                Log.Warning("OcrTextDetector: Failed to load image: {Path}", screenshotPath);
                return regions;
            }

            var result = await _engine.RecognizeAsync(bitmap);

            foreach (var line in result.Lines)
            {
                // Each line has words with individual bounding rects
                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    regions.Add(new TextRegion
                    {
                        Text = word.Text,
                        Bounds = new BoundingRect(
                            (int)rect.X, (int)rect.Y,
                            (int)rect.Width, (int)rect.Height),
                        Confidence = 0.9, // Windows OCR doesn't expose per-word confidence
                    });
                }

                // Also add the full line as a region (useful for labels that span multiple words)
                if (line.Words.Count > 1)
                {
                    var lineRect = ComputeLineBounds(line);
                    regions.Add(new TextRegion
                    {
                        Text = line.Text,
                        Bounds = lineRect,
                        Confidence = 0.85,
                    });
                }
            }

            sw.Stop();
            Log.Information("OcrTextDetector: Found {Count} text regions in {ElapsedMs}ms",
                regions.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "OcrTextDetector: Error detecting text in {Path}", screenshotPath);
        }

        return regions;
    }

    /// <summary>
    /// Detect text regions from raw PNG bytes.
    /// </summary>
    public async Task<List<TextRegion>> DetectTextAsync(byte[] imageBytes)
    {
        var sw = Stopwatch.StartNew();
        var regions = new List<TextRegion>();

        try
        {
            using var bitmap = await LoadSoftwareBitmapFromBytesAsync(imageBytes);
            if (bitmap == null)
            {
                Log.Warning("OcrTextDetector: Failed to load image from bytes");
                return regions;
            }

            var result = await _engine.RecognizeAsync(bitmap);

            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    regions.Add(new TextRegion
                    {
                        Text = word.Text,
                        Bounds = new BoundingRect(
                            (int)rect.X, (int)rect.Y,
                            (int)rect.Width, (int)rect.Height),
                        Confidence = 0.9,
                    });
                }

                if (line.Words.Count > 1)
                {
                    var lineRect = ComputeLineBounds(line);
                    regions.Add(new TextRegion
                    {
                        Text = line.Text,
                        Bounds = lineRect,
                        Confidence = 0.85,
                    });
                }
            }

            sw.Stop();
            Log.Information("OcrTextDetector: Found {Count} text regions from bytes in {ElapsedMs}ms",
                regions.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "OcrTextDetector: Error detecting text from bytes");
        }

        return regions;
    }

    /// <summary>
    /// Load a PNG file as a SoftwareBitmap for Windows OCR.
    /// </summary>
    private static async Task<SoftwareBitmap?> LoadSoftwareBitmapAsync(string imagePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            return await LoadSoftwareBitmapFromBytesAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OcrTextDetector: Failed to load bitmap from {Path}", imagePath);
            return null;
        }
    }

    /// <summary>
    /// Load raw image bytes as a SoftwareBitmap.
    /// </summary>
    private static async Task<SoftwareBitmap?> LoadSoftwareBitmapFromBytesAsync(byte[] imageBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(imageBytes.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OcrTextDetector: Failed to decode image bytes");
            return null;
        }
    }

    /// <summary>
    /// Compute the bounding rect of an entire OCR line from its constituent words.
    /// </summary>
    private static BoundingRect ComputeLineBounds(OcrLine line)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.X + r.Width);
            maxY = Math.Max(maxY, r.Y + r.Height);
        }

        return new BoundingRect(
            (int)minX, (int)minY,
            (int)(maxX - minX), (int)(maxY - minY));
    }
}
