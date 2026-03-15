using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class ImageViewerControl : UserControl, IFileViewer
{
    private string? _filePath;
    private double _zoom = 1.0;
    private int _imageWidth;
    private int _imageHeight;
    private readonly ScaleTransform _imageScale = new(1, 1);

    public string? FilePath => _filePath;
    public bool IsDirty => false;
    public event Action? DisplayNameChanged;

    public ImageViewerControl()
    {
        InitializeComponent();
        ImageDisplay.RenderTransform = _imageScale;
        UpdateZoomDisplay();
        ImageScrollViewer.PointerWheelChanged += ImageScrollViewer_PointerWheelChanged;
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[ImageViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;
        ImageDisplay.IsVisible = false;

        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            return new Bitmap(stream);
        });

        _imageWidth = bitmap.PixelSize.Width;
        _imageHeight = bitmap.PixelSize.Height;
        ImageDisplay.Source = bitmap;
        ImageDisplay.IsVisible = true;
        LoadingText.IsVisible = false;

        // Show image info
        var fileSize = await Task.Run(() => new FileInfo(filePath).Length);
        ImageInfoText.Text = $"{_imageWidth} x {_imageHeight} | {FormatFileSize(fileSize)}";

        // Fit to view by default
        FitToView();

        // Re-fit when the scroll viewer gets its first layout measurement
        if (ImageScrollViewer.Bounds.Width <= 0)
        {
            void OnFirstLayout(object? s, EventArgs args)
            {
                ImageScrollViewer.LayoutUpdated -= OnFirstLayout;
                FitToView();
            }
            ImageScrollViewer.LayoutUpdated += OnFirstLayout;
        }

        FileLog.Write($"[ImageViewer] LoadFileAsync complete: {filePath}, {_imageWidth}x{_imageHeight}");
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.IsVisible = true;
    }

    public Task SaveAsync() => Task.CompletedTask;

    public string GetDisplayName()
    {
        return _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
    }

    private void FitToView()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        var viewWidth = ImageScrollViewer.Bounds.Width - 20;
        var viewHeight = ImageScrollViewer.Bounds.Height - 20;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        var scaleX = viewWidth / _imageWidth;
        var scaleY = viewHeight / _imageHeight;
        _zoom = Math.Min(scaleX, scaleY);
        if (_zoom > 1.0) _zoom = 1.0; // Don't upscale beyond 100%

        ApplyZoom();
    }

    private void ApplyZoom()
    {
        _imageScale.ScaleX = _zoom;
        _imageScale.ScaleY = _zoom;
        UpdateZoomDisplay();
    }

    private void UpdateZoomDisplay()
    {
        ZoomLevelText.Text = $"{(int)(_zoom * 100)}%";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        try { FitToView(); }
        catch (Exception ex) { FileLog.Write($"[ImageViewer] FitButton_Click FAILED: {ex.Message}"); }
    }

    private void ActualSizeButton_Click(object? sender, RoutedEventArgs e)
    {
        try { _zoom = 1.0; ApplyZoom(); }
        catch (Exception ex) { FileLog.Write($"[ImageViewer] ActualSizeButton_Click FAILED: {ex.Message}"); }
    }

    private void ZoomInButton_Click(object? sender, RoutedEventArgs e)
    {
        try { _zoom = Math.Min(_zoom * 1.25, 10.0); ApplyZoom(); }
        catch (Exception ex) { FileLog.Write($"[ImageViewer] ZoomInButton_Click FAILED: {ex.Message}"); }
    }

    private void ZoomOutButton_Click(object? sender, RoutedEventArgs e)
    {
        try { _zoom = Math.Max(_zoom / 1.25, 0.1); ApplyZoom(); }
        catch (Exception ex) { FileLog.Write($"[ImageViewer] ZoomOutButton_Click FAILED: {ex.Message}"); }
    }

    private void ImageScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            if (e.Delta.Y > 0)
                _zoom = Math.Min(_zoom * 1.15, 10.0);
            else
                _zoom = Math.Max(_zoom / 1.15, 0.1);

            ApplyZoom();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] PointerWheelChanged FAILED: {ex.Message}");
        }
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[ImageViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }
}
