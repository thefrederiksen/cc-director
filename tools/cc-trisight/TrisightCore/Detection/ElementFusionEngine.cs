using System.Diagnostics;
using Serilog;

namespace Trisight.Core.Detection;

/// <summary>
/// Merges detection results from UIA, OCR, and PixelAnalysis into a unified element map.
///
/// Strategy:
/// 1. Start with UIA elements as foundation (highest confidence, pixel-perfect)
/// 2. Match OCR text to UIA elements for cross-validation
/// 3. OCR text not overlapping any UIA element → synthetic element (UIA-missed)
/// 4. PixelAnalysis detections not overlapping UIA or OCR → add as custom-drawn elements
/// 5. Assign confidence scores — multi-source confirmation boosts confidence
/// 6. Deduplicate overlapping detections
/// 7. Assign sequential IDs
/// </summary>
public class ElementFusionEngine
{
    /// <summary>
    /// IoU threshold above which two detections are considered the "same" element.
    /// </summary>
    private const double MergeIouThreshold = 0.3;

    /// <summary>
    /// Maximum center distance (pixels) to consider OCR text as belonging to a UIA element.
    /// </summary>
    private const double OcrMatchDistanceThreshold = 50.0;

    /// <summary>
    /// Fuse all detection sources into a single unified element list.
    /// </summary>
    public List<DetectedElement> Fuse(
        List<DetectedElement> uiaElements,
        List<TextRegion> ocrRegions,
        List<VisualElement> pixelDetections)
    {
        var sw = Stopwatch.StartNew();
        var fused = new List<DetectedElement>();

        // Step 1: Start with UIA elements (highest confidence)
        foreach (var uia in uiaElements)
        {
            fused.Add(uia);
        }

        // Step 2: Match OCR text to UIA elements
        var unmatchedOcr = new List<TextRegion>();
        foreach (var ocr in ocrRegions)
        {
            var matched = FindBestUiaMatch(ocr, fused);
            if (matched != null)
            {
                // Cross-validate: OCR confirms UIA element's text
                matched.Sources |= DetectionSource.Ocr;
                matched.Confidence = Math.Min(1.0, matched.Confidence + 0.05);

                // If UIA element has no name but OCR found text, use the OCR text
                if (string.IsNullOrWhiteSpace(matched.Name) && !string.IsNullOrWhiteSpace(ocr.Text))
                {
                    matched.Name = ocr.Text;
                }
            }
            else
            {
                // Only keep word-level OCR misses (lines contain their words)
                if (ocr.Text.Split(' ').Length <= 3)
                {
                    unmatchedOcr.Add(ocr);
                }
            }
        }

        // Step 3: Unmatched OCR text → likely elements UIA missed
        foreach (var ocr in unmatchedOcr)
        {
            // Skip very short text (likely noise) or very small regions
            if (ocr.Text.Length < 2 || ocr.Bounds.Area < 50) continue;

            // Check it doesn't significantly overlap with an existing element
            if (OverlapsExisting(ocr.Bounds, fused, MergeIouThreshold)) continue;

            fused.Add(new DetectedElement
            {
                Type = GuessTypeFromOcrText(ocr.Text),
                Name = ocr.Text,
                Bounds = ExpandTextBounds(ocr.Bounds),
                IsEnabled = true,
                IsInteractable = true,
                Sources = DetectionSource.Ocr,
                Confidence = ocr.Confidence * 0.7, // Lower confidence for OCR-only
            });
        }

        // Step 4: PixelAnalysis detections that don't overlap UIA or OCR
        foreach (var pixel in pixelDetections)
        {
            var matched = FindOverlappingElement(pixel.Bounds, fused, MergeIouThreshold);
            if (matched != null)
            {
                // PixelAnalysis confirms an existing element
                matched.Sources |= DetectionSource.PixelAnalysis;
                matched.Confidence = Math.Min(1.0, matched.Confidence + 0.05);
            }
            else
            {
                // New element from PixelAnalysis (custom-drawn control)
                fused.Add(new DetectedElement
                {
                    Type = pixel.Type,
                    Name = "",
                    Bounds = pixel.Bounds,
                    IsEnabled = true,
                    IsInteractable = true,
                    Sources = DetectionSource.PixelAnalysis,
                    Confidence = pixel.Confidence * 0.8,
                });
            }
        }

        // Step 5: Deduplicate remaining overlaps
        fused = DeduplicateElements(fused);

        // Step 6: Assign sequential IDs
        for (int i = 0; i < fused.Count; i++)
        {
            fused[i].Id = i + 1;
        }

        sw.Stop();
        Log.Information("ElementFusionEngine: Fused {Count} elements " +
            "(UIA={Uia}, OCR unmatched={OcrNew}, PixelAnalysis={PixelNew}) in {ElapsedMs}ms",
            fused.Count, uiaElements.Count, unmatchedOcr.Count, pixelDetections.Count, sw.ElapsedMilliseconds);

        return fused;
    }

    /// <summary>
    /// Find the best matching UIA element for an OCR text region.
    /// Matches on spatial overlap and text similarity.
    /// </summary>
    private static DetectedElement? FindBestUiaMatch(TextRegion ocr, List<DetectedElement> elements)
    {
        DetectedElement? best = null;
        double bestScore = 0;

        foreach (var elem in elements)
        {
            // Check spatial proximity
            double iou = ocr.Bounds.IoU(elem.Bounds);
            double dist = ocr.Bounds.CenterDistance(elem.Bounds);

            // Either significant overlap OR the text is inside the element's bounds
            bool spatialMatch = iou > 0.1
                || dist < OcrMatchDistanceThreshold
                || elem.Bounds.Contains(ocr.Bounds.CenterX, ocr.Bounds.CenterY);

            if (!spatialMatch) continue;

            // Compute a match score combining spatial and text similarity
            double textSim = TextSimilarity(ocr.Text, elem.Name);
            double score = (iou * 0.5) + (textSim * 0.3) + ((1.0 - Math.Min(dist, 200) / 200) * 0.2);

            if (score > bestScore)
            {
                bestScore = score;
                best = elem;
            }
        }

        // Require minimum score to match
        return bestScore > 0.15 ? best : null;
    }

    /// <summary>
    /// Find an element that overlaps with the given bounds above the IoU threshold.
    /// </summary>
    private static DetectedElement? FindOverlappingElement(BoundingRect bounds, List<DetectedElement> elements, double iouThreshold)
    {
        DetectedElement? best = null;
        double bestIou = 0;

        foreach (var elem in elements)
        {
            double iou = bounds.IoU(elem.Bounds);
            if (iou > iouThreshold && iou > bestIou)
            {
                bestIou = iou;
                best = elem;
            }
        }

        return best;
    }

    /// <summary>
    /// Check if a bounding rect overlaps significantly with any existing element.
    /// </summary>
    private static bool OverlapsExisting(BoundingRect bounds, List<DetectedElement> elements, double iouThreshold)
    {
        foreach (var elem in elements)
        {
            if (bounds.IoU(elem.Bounds) > iouThreshold)
                return true;

            // Also check if the OCR text is contained within a UIA element
            if (elem.Bounds.Contains(bounds.CenterX, bounds.CenterY))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Remove duplicate elements that overlap too much, keeping the higher-confidence one.
    /// </summary>
    private static List<DetectedElement> DeduplicateElements(List<DetectedElement> elements)
    {
        var keep = new List<DetectedElement>();

        // Sort by confidence descending — keep higher-confidence elements
        var sorted = elements.OrderByDescending(e => e.Confidence).ToList();

        foreach (var elem in sorted)
        {
            bool isDuplicate = false;
            foreach (var kept in keep)
            {
                if (elem.Bounds.IoU(kept.Bounds) > 0.5)
                {
                    isDuplicate = true;
                    // Merge sources
                    kept.Sources |= elem.Sources;
                    break;
                }
            }

            if (!isDuplicate)
            {
                keep.Add(elem);
            }
        }

        return keep;
    }

    /// <summary>
    /// Expand OCR text bounding box to approximate the full interactive element.
    /// Text is usually inside a button/control with padding around it.
    /// </summary>
    private static BoundingRect ExpandTextBounds(BoundingRect textBounds)
    {
        const int padX = 8;
        const int padY = 4;

        return new BoundingRect(
            Math.Max(0, textBounds.X - padX),
            Math.Max(0, textBounds.Y - padY),
            textBounds.Width + padX * 2,
            textBounds.Height + padY * 2);
    }

    /// <summary>
    /// Simple text similarity (case-insensitive, based on common substring).
    /// </summary>
    private static double TextSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();

        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.8;

        // Character overlap
        int common = a.Intersect(b).Count();
        int total = Math.Max(a.Length, b.Length);
        return total > 0 ? (double)common / total : 0;
    }

    /// <summary>
    /// Guess the UI element type from OCR text content.
    /// </summary>
    private static string GuessTypeFromOcrText(string text)
    {
        var lower = text.Trim().ToLowerInvariant();

        // Common button labels
        if (lower is "ok" or "cancel" or "yes" or "no" or "save" or "close" or "open"
            or "apply" or "submit" or "send" or "delete" or "next" or "back"
            or "browse" or "search" or "sign in" or "log in" or "continue")
            return "Button";

        // Menu-like items (single words that might be menu headers)
        if (lower is "file" or "edit" or "view" or "help" or "tools" or "window"
            or "format" or "insert" or "options" or "settings")
            return "MenuItem";

        // Default to text label
        return "Text";
    }
}
