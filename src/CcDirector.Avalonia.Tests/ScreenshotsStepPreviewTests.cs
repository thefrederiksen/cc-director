using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Screenshots step shows the newest images from the chosen folder, so the user confirms the
/// folder by recognising their own screenshots instead of trusting a count. An empty folder shows
/// no strip at all rather than an empty box.
/// </summary>
public class ScreenshotsStepPreviewTests
{
    // Smallest valid PNG (1x1) - enough for the decoder, nothing to look at.
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [AvaloniaFact]
    public async Task ChoosingAFolderWithImages_ShowsThumbnails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-shots-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Convert.FromBase64String(OnePixelPngBase64);
            File.WriteAllBytes(Path.Combine(dir, "one.png"), png);
            File.WriteAllBytes(Path.Combine(dir, "two.png"), png);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not an image");

            var dialog = new FirstRunWizardDialog(new AgentOptions());
            await dialog.SetScreenshotsFolderAsync(dir, "Chosen by you", imageCount: 2);

            Assert.True(dialog.ShotsPreviewStrip.IsVisible);
            Assert.Equal(2, dialog.ShotsPreviewRow.Children.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ChoosingAnEmptyFolder_ShowsNoStrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-shots-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dialog = new FirstRunWizardDialog(new AgentOptions());
            await dialog.SetScreenshotsFolderAsync(dir, "Chosen by you", imageCount: 0);

            Assert.False(dialog.ShotsPreviewStrip.IsVisible);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
