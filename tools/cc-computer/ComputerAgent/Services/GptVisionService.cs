using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace CCComputer.Agent.Services;

/// <summary>
/// Service for making GPT-4 Vision API calls to analyze screenshots.
/// Used for vision-based element detection when UI Automation fails.
/// </summary>
public class GptVisionService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _modelId;
    private bool _disposed;

    // Vision-capable models
    private static readonly HashSet<string> VisionModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4-turbo",
        "gpt-4-vision-preview"
    };

    public GptVisionService(string apiKey, string modelId = "gpt-4o")
    {
        _apiKey = apiKey;
        _modelId = VisionModels.Contains(modelId) ? modelId : "gpt-4o";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Analyze an image using GPT-4 Vision and return the response.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="prompt">The prompt describing what to analyze</param>
    /// <returns>The AI's response text</returns>
    public async Task<string> AnalyzeImageAsync(string imagePath, string prompt)
    {
        Log.Debug("GptVisionService.AnalyzeImageAsync: path={Path}, prompt={Prompt}", imagePath, prompt.Length > 100 ? prompt[..100] + "..." : prompt);

        if (!File.Exists(imagePath))
        {
            return $"ERROR: Image file not found: {imagePath}";
        }

        try
        {
            // Read and encode image as base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var base64Image = Convert.ToBase64String(imageBytes);
            var mimeType = GetMimeType(imagePath);

            // Build request payload
            var requestBody = new
            {
                model = _modelId,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = prompt
                            },
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = $"data:{mimeType};base64,{base64Image}",
                                    detail = "high"
                                }
                            }
                        }
                    }
                },
                max_tokens = 1000
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("v1/chat/completions", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("GptVisionService API error: {StatusCode} - {Error}", response.StatusCode, error);
                return $"ERROR: Vision API call failed: {response.StatusCode}";
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

            var messageContent = result
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            Log.Debug("GptVisionService response: {Response}", messageContent?.Length > 200 ? messageContent[..200] + "..." : messageContent);

            return messageContent ?? "ERROR: No response from vision model";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GptVisionService.AnalyzeImageAsync failed");
            return $"ERROR: Vision analysis failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Find an element in a screenshot and return its coordinates.
    /// </summary>
    /// <param name="imagePath">Path to the screenshot</param>
    /// <param name="elementDescription">Description of the element to find (e.g., "Save As menu item")</param>
    /// <param name="cropRegion">Optional crop region (x, y, width, height). When specified, only this
    /// region of the screenshot is sent to the vision model, and returned coordinates are translated
    /// back to full-screen space. This improves accuracy by giving the model a smaller coordinate space.</param>
    /// <returns>Coordinates as "x,y" or an error message</returns>
    public async Task<string> FindElementCoordinatesAsync(
        string imagePath,
        string elementDescription,
        (int X, int Y, int Width, int Height)? cropRegion = null)
    {
        // If a crop region is specified, crop the screenshot first
        string imageToAnalyze = imagePath;
        string? tempCroppedPath = null;
        int offsetX = 0, offsetY = 0;

        if (cropRegion.HasValue)
        {
            var crop = cropRegion.Value;
            offsetX = crop.X;
            offsetY = crop.Y;

            try
            {
                tempCroppedPath = Path.Combine(
                    Path.GetDirectoryName(imagePath) ?? Path.GetTempPath(),
                    $"crop_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

                CropImage(imagePath, tempCroppedPath, crop.X, crop.Y, crop.Width, crop.Height);
                imageToAnalyze = tempCroppedPath;

                Log.Debug("GptVisionService: Cropped screenshot to region ({X},{Y},{W},{H}) -> {Path}",
                    crop.X, crop.Y, crop.Width, crop.Height, tempCroppedPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GptVisionService: Failed to crop screenshot, using full image");
                imageToAnalyze = imagePath;
                offsetX = 0;
                offsetY = 0;
            }
        }

        try
        {
            var prompt = $@"Look at this screenshot. Find the UI element: '{elementDescription}'

Return ONLY the approximate pixel coordinates (x, y) of the CENTER of that element.
Format your response as just two numbers separated by a comma, like: 450,320

If the element is not visible in the screenshot, respond with exactly: NOT_FOUND

Do not include any other text, just the coordinates or NOT_FOUND.";

            var response = await AnalyzeImageAsync(imageToAnalyze, prompt);

            // Parse the response
            if (response.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return $"NOT_FOUND: Could not locate '{elementDescription}' in screenshot";
            }

            // Try to extract coordinates
            var cleaned = response.Trim();
            var parts = cleaned.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out var x) &&
                int.TryParse(parts[1].Trim(), out var y))
            {
                // Translate cropped coordinates back to full-screen space
                x += offsetX;
                y += offsetY;
                return $"{x},{y}";
            }

            // If parsing failed, return the raw response
            return $"PARSE_ERROR: Got response '{cleaned}' - expected 'x,y' format";
        }
        finally
        {
            // Clean up temp cropped file
            if (tempCroppedPath != null && File.Exists(tempCroppedPath))
            {
                try { File.Delete(tempCroppedPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Crop an image file to the specified region and save to a new file.
    /// Uses System.Drawing for simplicity on Windows.
    /// </summary>
    private static void CropImage(string sourcePath, string destPath, int x, int y, int width, int height)
    {
        var imageBytes = File.ReadAllBytes(sourcePath);
        using var bitmap = SkiaSharp.SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException($"Failed to decode image: {sourcePath}");

        // Clamp crop region to image bounds
        x = Math.Max(0, Math.Min(x, bitmap.Width - 1));
        y = Math.Max(0, Math.Min(y, bitmap.Height - 1));
        width = Math.Min(width, bitmap.Width - x);
        height = Math.Min(height, bitmap.Height - y);

        var subset = new SkiaSharp.SKRectI(x, y, x + width, y + height);
        using var cropped = new SkiaSharp.SKBitmap();
        if (!bitmap.ExtractSubset(cropped, subset))
            throw new InvalidOperationException("Failed to extract image subset");

        using var image = SkiaSharp.SKImage.FromBitmap(cropped);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Describe what's visible in a screenshot.
    /// </summary>
    /// <param name="imagePath">Path to the screenshot</param>
    /// <returns>Description of the screenshot contents</returns>
    public async Task<string> DescribeScreenshotAsync(string imagePath)
    {
        var prompt = @"Describe what you see in this screenshot. Focus on:
1. What application is shown
2. What dialog or window is open
3. Any buttons, text fields, or important UI elements visible
4. The current state (e.g., 'Save As dialog is open', 'compose email window')

Be concise but specific.";

        return await AnalyzeImageAsync(imagePath, prompt);
    }

    /// <summary>
    /// Check if a specific condition is visible in a screenshot.
    /// </summary>
    /// <param name="imagePath">Path to the screenshot</param>
    /// <param name="condition">The condition to check (e.g., "is the file save dialog open?")</param>
    /// <returns>true/false with explanation</returns>
    public async Task<string> VerifyConditionAsync(string imagePath, string condition)
    {
        var prompt = $@"Look at this screenshot and answer this question: {condition}

Respond with either:
- YES: [brief explanation]
- NO: [brief explanation]

Be direct and specific.";

        return await AnalyzeImageAsync(imagePath, prompt);
    }

    private static string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
