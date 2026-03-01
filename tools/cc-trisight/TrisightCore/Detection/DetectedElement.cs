using System.Text.Json.Serialization;

namespace Trisight.Core.Detection;

/// <summary>
/// Source that detected an element.
/// </summary>
[Flags]
public enum DetectionSource
{
    None = 0,
    Uia = 1,
    Ocr = 2,
    PixelAnalysis = 4,
}

/// <summary>
/// Axis-aligned bounding rectangle in screen pixel coordinates.
/// </summary>
public readonly record struct BoundingRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
    public int Area => Width * Height;

    /// <summary>
    /// Checks if a point is inside this rectangle.
    /// </summary>
    public bool Contains(int px, int py) =>
        px >= Left && px <= Right && py >= Top && py <= Bottom;

    /// <summary>
    /// Computes the intersection-over-union with another rectangle.
    /// </summary>
    public double IoU(BoundingRect other)
    {
        int overlapX = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        int overlapY = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        int intersection = overlapX * overlapY;
        if (intersection == 0) return 0;
        int union = Area + other.Area - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }

    /// <summary>
    /// Euclidean distance between centers.
    /// </summary>
    public double CenterDistance(BoundingRect other)
    {
        double dx = CenterX - other.CenterX;
        double dy = CenterY - other.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// A unified detected UI element combining data from all detection sources.
/// </summary>
public class DetectedElement
{
    /// <summary>
    /// Sequential element ID assigned during fusion (1-based, used for LLM references).
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Control type (Button, TextBox, MenuItem, CheckBox, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// Human-readable name/label.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Bounding rectangle in screen coordinates.
    /// </summary>
    [JsonPropertyName("bbox")]
    public BoundingRect Bounds { get; set; }

    /// <summary>
    /// UIA AutomationId (if available).
    /// </summary>
    [JsonPropertyName("automationId")]
    public string? AutomationId { get; set; }

    /// <summary>
    /// Whether the element is enabled for interaction.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the element can be interacted with (clicked, typed into, etc.).
    /// </summary>
    [JsonPropertyName("interactable")]
    public bool IsInteractable { get; set; } = true;

    /// <summary>
    /// Current state description (checked, unchecked, focused, expanded, etc.).
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// UIA ClassName (if available).
    /// </summary>
    [JsonIgnore]
    public string? ClassName { get; set; }

    /// <summary>
    /// Sources that detected this element.
    /// </summary>
    [JsonPropertyName("source")]
    public DetectionSource Sources { get; set; }

    /// <summary>
    /// Confidence score (0.0–1.0). Higher when confirmed by multiple sources.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Click target — center of the bounding rect.
    /// </summary>
    [JsonPropertyName("center")]
    public int[] Center => [Bounds.CenterX, Bounds.CenterY];

    /// <summary>
    /// Bounding box as [x1, y1, x2, y2] for serialization.
    /// </summary>
    [JsonPropertyName("bboxCoords")]
    public int[] BboxCoords => [Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom];

    public override string ToString() =>
        $"[{Id}] {Type}: \"{Name}\" at ({Bounds.X},{Bounds.Y},{Bounds.Width}x{Bounds.Height})";
}

/// <summary>
/// Text region detected by OCR.
/// </summary>
public class TextRegion
{
    public string Text { get; set; } = "";
    public BoundingRect Bounds { get; set; }
    public double Confidence { get; set; }
}

/// <summary>
/// Visual element detected by YOLO.
/// </summary>
public class VisualElement
{
    public string Type { get; set; } = "";
    public BoundingRect Bounds { get; set; }
    public double Confidence { get; set; }
}

/// <summary>
/// Result of running the full detection pipeline on a screenshot.
/// </summary>
public class DetectionResult
{
    /// <summary>
    /// Window title being analyzed.
    /// </summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>
    /// Window handle used for UIA.
    /// </summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// All detected elements after fusion.
    /// </summary>
    public List<DetectedElement> Elements { get; set; } = [];

    /// <summary>
    /// Raw UIA element count before fusion.
    /// </summary>
    public int UiaElementCount { get; set; }

    /// <summary>
    /// Raw OCR text region count before fusion.
    /// </summary>
    public int OcrRegionCount { get; set; }

    /// <summary>
    /// Raw pixel analysis detection count before fusion.
    /// </summary>
    public int PixelAnalysisCount { get; set; }

    /// <summary>
    /// Total detection time in milliseconds.
    /// </summary>
    public long DetectionTimeMs { get; set; }

    /// <summary>
    /// Path to annotated screenshot (if rendered).
    /// </summary>
    public string? AnnotatedScreenshotPath { get; set; }
}
