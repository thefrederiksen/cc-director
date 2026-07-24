using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests;

public class ScreenshotLocatorTests
{
    [Fact]
    public void ParseMacScreencaptureLocation_TildePath_ExpandsToHome()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("~/Desktop/Shots\n", "/Users/testuser");

        Assert.Equal("/Users/testuser/Desktop/Shots", result);
    }

    [Fact]
    public void ParseMacScreencaptureLocation_BareTilde_IsHome()
    {
        Assert.Equal("/Users/testuser", ScreenshotLocator.ParseMacScreencaptureLocation("~", "/Users/testuser"));
    }

    [Fact]
    public void ParseMacScreencaptureLocation_AbsolutePath_ReturnedAsIs()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("/Users/testuser/Pictures/Caps\n", "/Users/testuser");

        Assert.Equal("/Users/testuser/Pictures/Caps", result);
    }

    [Fact]
    public void ParseMacScreencaptureLocation_Quoted_Trimmed()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("\"~/Desktop\"", "/Users/testuser");

        Assert.Equal("/Users/testuser/Desktop", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void ParseMacScreencaptureLocation_EmptyOrUnset_ReturnsNull(string? stdout)
    {
        Assert.Null(ScreenshotLocator.ParseMacScreencaptureLocation(stdout, "/Users/testuser"));
    }

    [Fact]
    public void Detect_OnThisPlatform_DoesNotThrow()
    {
        // Smoke: detection must never throw regardless of environment; it returns a path or null.
        var ex = Record.Exception(() => ScreenshotLocator.Detect());

        Assert.Null(ex);
    }

    [Fact]
    public void DetectCandidates_ReturnsOnlyExistingFolders_WithProvenance_NoDuplicates()
    {
        var candidates = ScreenshotLocator.DetectCandidates();

        foreach (var c in candidates)
        {
            Assert.True(Directory.Exists(c.Path), $"candidate does not exist: {c.Path}");
            Assert.False(string.IsNullOrWhiteSpace(c.Provenance));
        }

        var distinct = candidates.Select(c => c.Path.ToLowerInvariant()).Distinct().Count();
        Assert.Equal(candidates.Count, distinct);
    }

    [Fact]
    public void Detect_AgreesWithTheFirstCandidate()
    {
        // Detect() is the Settings-page entry point; it must be the wizard's best candidate, not a
        // separately-derived answer that could drift.
        var candidates = ScreenshotLocator.DetectCandidates();
        Assert.Equal(candidates.FirstOrDefault()?.Path, ScreenshotLocator.Detect());
    }

    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("shot.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("anim.gif", true)]
    [InlineData("old.bmp", true)]
    [InlineData("modern.webp", true)]
    [InlineData("clip.mp4", false)]
    [InlineData("notes.txt", false)]
    [InlineData("noext", false)]
    public void IsImageFile_ClassifiesByExtension(string name, bool expected)
    {
        Assert.Equal(expected, ScreenshotLocator.IsImageFile(name));
    }

    [Fact]
    public void CountImages_CountsOnlyImageFiles_TopLevel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shots-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.png"), "x");
            File.WriteAllText(Path.Combine(dir, "b.jpg"), "x");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "nested"));
            File.WriteAllText(Path.Combine(dir, "nested", "d.png"), "x");

            Assert.Equal(2, ScreenshotLocator.CountImages(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CountImages_MissingFolder_ReturnsZero_NeverThrows()
    {
        Assert.Equal(0, ScreenshotLocator.CountImages(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }
}
