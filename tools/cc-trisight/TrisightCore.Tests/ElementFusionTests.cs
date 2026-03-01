using System.Reflection;
using Trisight.Core.Detection;
using Xunit;

namespace Trisight.Core.Tests;

public class ElementFusionTests
{
    // Helper to invoke the private static TextSimilarity(string a, string b) method.
    private static double InvokeTextSimilarity(string? a, string? b)
    {
        var method = typeof(ElementFusionEngine).GetMethod(
            "TextSimilarity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (double)method.Invoke(null, [a, b])!;
    }

    // Helper to invoke the private static GuessTypeFromOcrText(string text) method.
    private static string InvokeGuessTypeFromOcrText(string text)
    {
        var method = typeof(ElementFusionEngine).GetMethod(
            "GuessTypeFromOcrText",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (string)method.Invoke(null, [text])!;
    }

    // Helper to invoke the private static DeduplicateElements method.
    private static List<DetectedElement> InvokeDeduplicateElements(List<DetectedElement> elements)
    {
        var method = typeof(ElementFusionEngine).GetMethod(
            "DeduplicateElements",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (List<DetectedElement>)method.Invoke(null, [elements])!;
    }

    // ---------------------------------------------------------------
    // TextSimilarity
    // ---------------------------------------------------------------

    [Fact]
    public void TextSimilarity_IdenticalStrings_Returns1()
    {
        double sim = InvokeTextSimilarity("hello", "hello");

        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void TextSimilarity_CompletelyDifferentStrings_Returns0()
    {
        // "abc" and "xyz" share no characters
        double sim = InvokeTextSimilarity("abc", "xyz");

        Assert.Equal(0.0, sim);
    }

    [Fact]
    public void TextSimilarity_PartialMatch_ContainsSubstring_Returns08()
    {
        // "button" contains "but", so the method returns 0.8
        double sim = InvokeTextSimilarity("button", "but");

        Assert.Equal(0.8, sim);
    }

    [Fact]
    public void TextSimilarity_NullOrEmpty_Returns0()
    {
        Assert.Equal(0.0, InvokeTextSimilarity(null, "hello"));
        Assert.Equal(0.0, InvokeTextSimilarity("hello", null));
        Assert.Equal(0.0, InvokeTextSimilarity("", "hello"));
        Assert.Equal(0.0, InvokeTextSimilarity("hello", ""));
        Assert.Equal(0.0, InvokeTextSimilarity(null, null));
    }

    // ---------------------------------------------------------------
    // GuessTypeFromOcrText
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("OK", "Button")]
    [InlineData("Cancel", "Button")]
    [InlineData("Save", "Button")]
    [InlineData("submit", "Button")]
    [InlineData("Sign In", "Button")]
    public void GuessTypeFromOcrText_ButtonLabels_ReturnsButton(string text, string expected)
    {
        string result = InvokeGuessTypeFromOcrText(text);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("File", "MenuItem")]
    [InlineData("Edit", "MenuItem")]
    [InlineData("View", "MenuItem")]
    [InlineData("Help", "MenuItem")]
    [InlineData("Settings", "MenuItem")]
    public void GuessTypeFromOcrText_MenuLabels_ReturnsMenuItem(string text, string expected)
    {
        string result = InvokeGuessTypeFromOcrText(text);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Some random label")]
    [InlineData("Username")]
    [InlineData("Total: $42.00")]
    public void GuessTypeFromOcrText_OtherText_ReturnsText(string text)
    {
        string result = InvokeGuessTypeFromOcrText(text);

        Assert.Equal("Text", result);
    }

    // ---------------------------------------------------------------
    // DeduplicateElements
    // ---------------------------------------------------------------

    [Fact]
    public void DeduplicateElements_RemovesOverlappingElements_KeepsHigherConfidence()
    {
        // Two elements at the exact same position -- high overlap (IoU = 1.0)
        var elements = new List<DetectedElement>
        {
            new()
            {
                Type = "Button",
                Name = "OK",
                Bounds = new BoundingRect(10, 10, 80, 30),
                Confidence = 0.6,
                Sources = DetectionSource.Ocr,
            },
            new()
            {
                Type = "Button",
                Name = "OK",
                Bounds = new BoundingRect(10, 10, 80, 30),
                Confidence = 0.9,
                Sources = DetectionSource.Uia,
            },
        };

        var result = InvokeDeduplicateElements(elements);

        // Should keep only one element -- the higher confidence one
        Assert.Single(result);
        Assert.Equal(0.9, result[0].Confidence);
        // Sources should be merged
        Assert.True(result[0].Sources.HasFlag(DetectionSource.Uia));
        Assert.True(result[0].Sources.HasFlag(DetectionSource.Ocr));
    }

    [Fact]
    public void DeduplicateElements_KeepsNonOverlappingElements()
    {
        var elements = new List<DetectedElement>
        {
            new()
            {
                Type = "Button",
                Name = "OK",
                Bounds = new BoundingRect(10, 10, 80, 30),
                Confidence = 0.9,
                Sources = DetectionSource.Uia,
            },
            new()
            {
                Type = "Button",
                Name = "Cancel",
                Bounds = new BoundingRect(200, 10, 80, 30),
                Confidence = 0.9,
                Sources = DetectionSource.Uia,
            },
        };

        var result = InvokeDeduplicateElements(elements);

        Assert.Equal(2, result.Count);
    }
}
