using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class TextViewerControl : UserControl, IFileViewer
{
    private const int MaxFileSizeBytes = 512_000; // 500 KB

    private string? _filePath;
    private bool _isDirty;
    private bool _suppressTextChanged;
    private bool _wordWrap = true;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;
    public event Action? DisplayNameChanged;

    public TextViewerControl()
    {
        InitializeComponent();
        UpdateWrapButton();
        EditorBox.AddHandler(TextInputEvent, EditorBox_TextInput, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[TextViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;
        EditorBox.IsVisible = false;

        var (content, truncated) = await Task.Run(() =>
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaxFileSizeBytes)
            {
                using var reader = new StreamReader(filePath);
                var buf = new char[MaxFileSizeBytes];
                int read = reader.Read(buf, 0, buf.Length);
                return (new string(buf, 0, read), true);
            }
            return (File.ReadAllText(filePath), false);
        });

        _suppressTextChanged = true;
        EditorBox.Text = content;
        _suppressTextChanged = false;

        if (truncated)
        {
            EditorBox.IsReadOnly = true;
            FilePathText.Text = $"{filePath}  [TRUNCATED - file exceeds 500 KB]";
        }

        LoadingText.IsVisible = false;
        EditorBox.IsVisible = true;
        UpdateSaveButton();

        FileLog.Write($"[TextViewer] LoadFileAsync complete: {filePath}, length={content.Length}, truncated={truncated}");
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.IsVisible = true;
    }

    public async Task SaveAsync()
    {
        if (_filePath == null || !_isDirty)
            return;

        FileLog.Write($"[TextViewer] Save: {_filePath}");
        var text = EditorBox.Text ?? "";
        await Task.Run(() => File.WriteAllText(_filePath, text));
        _isDirty = false;
        UpdateSaveButton();
        DisplayNameChanged?.Invoke();
        FileLog.Write($"[TextViewer] Save complete: {_filePath}");
    }

    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        return _isDirty ? $"*{name}" : name;
    }

    private void UpdateSaveButton()
    {
        SaveButton.IsVisible = _isDirty;
    }

    private void UpdateWrapButton()
    {
        WrapToggleButton.Content = _wordWrap ? "No Wrap" : "Wrap";
    }

    private void WrapToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _wordWrap = !_wordWrap;
            EditorBox.TextWrapping = _wordWrap
                ? global::Avalonia.Media.TextWrapping.Wrap
                : global::Avalonia.Media.TextWrapping.NoWrap;
            UpdateWrapButton();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] WrapToggleButton_Click FAILED: {ex.Message}");
        }
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] SaveButton_Click FAILED: {ex.Message}");
        }
    }

    private void EditorBox_TextInput(object? sender, TextInputEventArgs e)
    {
        try
        {
            if (_suppressTextChanged) return;

            if (!_isDirty)
            {
                _isDirty = true;
                UpdateSaveButton();
                DisplayNameChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] EditorBox_TextInput FAILED: {ex.Message}");
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            try
            {
                await SaveAsync();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TextViewer] Ctrl+S FAILED: {ex.Message}");
            }
        }
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[TextViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }
}
