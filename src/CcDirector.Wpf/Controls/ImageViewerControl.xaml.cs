using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

public partial class ImageViewerControl : UserControl, IFileViewer
{
    private string? _filePath;
    private double _zoom = 1.0;
    private int _imageWidth;
    private int _imageHeight;

    public string? FilePath => _filePath;
    public bool IsDirty => false;

    public ImageViewerControl()
    {
        InitializeComponent();
        UpdateZoomDisplay();
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[ImageViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        FilePathText.Text = filePath;
        LoadingText.Visibility = Visibility.Visible;
        ImageDisplay.Visibility = Visibility.Collapsed;

        var bitmap = await Task.Run(() =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(filePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        });

        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        ImageDisplay.Source = bitmap;
        ImageDisplay.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;

        // Show image info
        var fileSize = await Task.Run(() => new FileInfo(filePath).Length);
        ImageInfoText.Text = $"{_imageWidth} x {_imageHeight} | {FormatFileSize(fileSize)}";

        // Fit to view by default (may fail if layout hasn't happened yet)
        FitToView();

        // Re-fit when the scroll viewer gets its first layout measurement
        void OnFirstLayout(object s, SizeChangedEventArgs args)
        {
            ImageScrollViewer.SizeChanged -= OnFirstLayout;
            FitToView();
        }
        if (ImageScrollViewer.ActualWidth <= 0)
            ImageScrollViewer.SizeChanged += OnFirstLayout;

        FileLog.Write($"[ImageViewer] LoadFileAsync complete: {filePath}, {_imageWidth}x{_imageHeight}");
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.Visibility = Visibility.Visible;
    }

    public Task SaveAsync() => Task.CompletedTask;

    public string GetDisplayName()
    {
        return _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
    }

    private void FitToView()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        var viewWidth = ImageScrollViewer.ActualWidth - 20;
        var viewHeight = ImageScrollViewer.ActualHeight - 20;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        var scaleX = viewWidth / _imageWidth;
        var scaleY = viewHeight / _imageHeight;
        _zoom = Math.Min(scaleX, scaleY);
        if (_zoom > 1.0) _zoom = 1.0; // Don't upscale beyond 100%

        ApplyZoom();
    }

    private void ApplyZoom()
    {
        ImageScale.ScaleX = _zoom;
        ImageScale.ScaleY = _zoom;
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

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FitToView();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] FitButton_Click FAILED: {ex.Message}");
        }
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _zoom = 1.0;
            ApplyZoom();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] ActualSizeButton_Click FAILED: {ex.Message}");
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _zoom = Math.Min(_zoom * 1.25, 10.0);
            ApplyZoom();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] ZoomInButton_Click FAILED: {ex.Message}");
        }
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _zoom = Math.Max(_zoom / 1.25, 0.1);
            ApplyZoom();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] ZoomOutButton_Click FAILED: {ex.Message}");
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            if (e.Delta > 0)
                _zoom = Math.Min(_zoom * 1.15, 10.0);
            else
                _zoom = Math.Max(_zoom / 1.15, 0.1);

            ApplyZoom();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ImageViewer] ImageScrollViewer_PreviewMouseWheel FAILED: {ex.Message}");
        }
    }
}
