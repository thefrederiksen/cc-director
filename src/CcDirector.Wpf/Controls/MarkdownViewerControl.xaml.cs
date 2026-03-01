using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CcDirector.Wpf.Helpers;

namespace CcDirector.Wpf.Controls;

public partial class MarkdownViewerControl : UserControl, IFileViewer
{
    private string? _filePath;
    private string _rawContent = "";
    private bool _isPreviewMode = true;
    private bool _isDirty;
    private bool _suppressTextChanged;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;

    public MarkdownViewerControl()
    {
        InitializeComponent();
        UpdateModeButton();
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[MarkdownViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        LoadingText.Visibility = Visibility.Visible;
        PreviewViewer.Visibility = Visibility.Collapsed;
        EditorBox.Visibility = Visibility.Collapsed;

        var content = await Task.Run(() => File.ReadAllText(filePath));
        _rawContent = content;

        if (_isPreviewMode)
            RenderPreview();
        else
            ShowEditor();

        LoadingText.Visibility = Visibility.Collapsed;
        FileLog.Write($"[MarkdownViewer] LoadFileAsync complete: {filePath}, length={content.Length}");
    }

    /// <summary>Shows a load error message in the loading overlay.</summary>
    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.Visibility = Visibility.Visible;
    }

    public async Task SaveAsync()
    {
        if (_filePath == null || !_isDirty)
            return;

        FileLog.Write($"[MarkdownViewer] Save: {_filePath}");
        await Task.Run(() => File.WriteAllText(_filePath, _rawContent));
        _isDirty = false;
        UpdateSaveButton();
        UpdateTabHeader();
        FileLog.Write($"[MarkdownViewer] Save complete: {_filePath}");
    }

    /// <summary>
    /// Returns the display name for the tab header, including dirty indicator.
    /// </summary>
    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled.md";
        return _isDirty ? $"*{name}" : name;
    }

    private void RenderPreview()
    {
        _isPreviewMode = true;

        // Capture editor content if switching from editor
        if (EditorBox.Visibility == Visibility.Visible)
            _rawContent = EditorBox.Text;

        var doc = MarkdownFlowDocumentRenderer.Render(_rawContent);
        PreviewViewer.Document = doc;
        PreviewViewer.Visibility = Visibility.Visible;
        EditorBox.Visibility = Visibility.Collapsed;
        UpdateModeButton();
        UpdateSaveButton();
    }

    private void ShowEditor()
    {
        _isPreviewMode = false;
        _suppressTextChanged = true;
        EditorBox.Text = _rawContent;
        _suppressTextChanged = false;
        EditorBox.Visibility = Visibility.Visible;
        PreviewViewer.Visibility = Visibility.Collapsed;
        UpdateModeButton();
        UpdateSaveButton();
        EditorBox.Focus();
    }

    private void UpdateModeButton()
    {
        ModeToggleButton.Content = _isPreviewMode ? "Source" : "Preview";
        ModeToggleButton.ToolTip = _isPreviewMode
            ? "Switch to source editor"
            : "Switch to rendered preview";
    }

    private void UpdateSaveButton()
    {
        SaveButton.Visibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabHeader()
    {
        // Walk up to find the TabItem and update its header text
        if (Parent is TabItem tab)
        {
            if (tab.Header is StackPanel panel && panel.Children.Count > 0 &&
                panel.Children[0] is TextBlock headerText)
            {
                headerText.Text = GetDisplayName();
            }
        }
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isPreviewMode)
                ShowEditor();
            else
                RenderPreview();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] ModeToggleButton_Click FAILED: {ex.Message}");
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] SaveButton_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (_suppressTextChanged)
                return;

            _rawContent = EditorBox.Text;
            if (!_isDirty)
            {
                _isDirty = true;
                UpdateSaveButton();
                UpdateTabHeader();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] EditorBox_TextChanged FAILED: {ex.Message}");
        }
    }

    private async void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] SaveCommand_Executed FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _isDirty;
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.ContextMenu is ContextMenu menu)
            {
                menu.PlacementTarget = btn;
                menu.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }

    private void OpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[MarkdownViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] OpenDefault FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open file:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{FilePath}\""
            });
            FileLog.Write($"[MarkdownViewer] Opened 'Open with' dialog: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] OpenWith FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open file:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
