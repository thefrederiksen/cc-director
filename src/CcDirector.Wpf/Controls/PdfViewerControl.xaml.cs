using System.IO;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

public partial class PdfViewerControl : UserControl, IFileViewer
{
    private string? _filePath;
    private bool _webViewReady;

    public string? FilePath => _filePath;
    public bool IsDirty => false;

    public PdfViewerControl()
    {
        InitializeComponent();
        Loaded += PdfViewerControl_Loaded;
    }

    private async void PdfViewerControl_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[PdfViewer] Initializing WebView2");
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CcDirector", "webview2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);
            await PdfWebView.EnsureCoreWebView2Async(env);
            _webViewReady = true;
            FileLog.Write("[PdfViewer] WebView2 initialized");

            if (_filePath != null)
                NavigateToPdf(_filePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PdfViewer] WebView2 init FAILED: {ex.Message}");
            LoadingText.Text = $"PDF viewer unavailable: {ex.Message}";
        }
    }

    public Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[PdfViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        FilePathText.Text = filePath;
        LoadingText.Visibility = Visibility.Visible;

        if (_webViewReady)
            NavigateToPdf(filePath);

        return Task.CompletedTask;
    }

    private void NavigateToPdf(string filePath)
    {
        var uri = new Uri(filePath).AbsoluteUri;
        FileLog.Write($"[PdfViewer] Navigating to: {uri}");
        PdfWebView.Source = new Uri(uri);
        LoadingText.Visibility = Visibility.Collapsed;
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
}
