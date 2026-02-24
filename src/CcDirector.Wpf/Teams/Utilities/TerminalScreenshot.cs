using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf.Teams.Utilities;

/// <summary>
/// Utility for capturing screenshots of TerminalControl.
/// </summary>
public static class TerminalScreenshot
{
    /// <summary>
    /// Capture the terminal control as a PNG image.
    /// Must be called on the UI thread via Dispatcher.
    /// </summary>
    public static byte[] Capture(TerminalControl terminal)
    {
        // Get the actual rendered size
        var width = (int)terminal.ActualWidth;
        var height = (int)terminal.ActualHeight;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Terminal has no size");

        // Create render target
        var dpiScale = VisualTreeHelper.GetDpi(terminal);
        var renderTarget = new RenderTargetBitmap(
            (int)(width * dpiScale.DpiScaleX),
            (int)(height * dpiScale.DpiScaleY),
            dpiScale.PixelsPerInchX,
            dpiScale.PixelsPerInchY,
            PixelFormats.Pbgra32);

        // Render the control
        renderTarget.Render(terminal);

        // Encode to PNG
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderTarget));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Capture the terminal and save to a temp file. Returns the file path.
    /// Must be called on the UI thread via Dispatcher.
    /// </summary>
    public static string CaptureToTempFile(TerminalControl terminal)
    {
        var bytes = Capture(terminal);
        var tempPath = Path.Combine(Path.GetTempPath(), $"cc_director_snap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        File.WriteAllBytes(tempPath, bytes);
        return tempPath;
    }
}
