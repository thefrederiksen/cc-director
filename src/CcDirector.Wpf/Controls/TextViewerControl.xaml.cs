using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

public partial class TextViewerControl : UserControl, IFileViewer
{
    private const int MaxFileSizeBytes = 512_000; // 500 KB

    private string? _filePath;
    private bool _isDirty;
    private bool _suppressTextChanged;
    private bool _wordWrap = true;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;

    public TextViewerControl()
    {
        InitializeComponent();
        UpdateWrapButton();
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[TextViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        LoadingText.Visibility = Visibility.Visible;
        EditorBox.Visibility = Visibility.Collapsed;

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

        LoadingText.Visibility = Visibility.Collapsed;
        EditorBox.Visibility = Visibility.Visible;
        UpdateSaveButton();

        FileLog.Write($"[TextViewer] LoadFileAsync complete: {filePath}, length={content.Length}, truncated={truncated}");
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.Visibility = Visibility.Visible;
    }

    public async Task SaveAsync()
    {
        if (_filePath == null || !_isDirty)
            return;

        FileLog.Write($"[TextViewer] Save: {_filePath}");
        var text = EditorBox.Text;
        await Task.Run(() => File.WriteAllText(_filePath, text));
        _isDirty = false;
        UpdateSaveButton();
        UpdateTabHeader();
        FileLog.Write($"[TextViewer] Save complete: {_filePath}");
    }

    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        return _isDirty ? $"*{name}" : name;
    }

    private void UpdateSaveButton()
    {
        SaveButton.Visibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWrapButton()
    {
        WrapToggleButton.Content = _wordWrap ? "No Wrap" : "Wrap";
        WrapToggleButton.ToolTip = _wordWrap
            ? "Disable word wrapping"
            : "Enable word wrapping";
    }

    private void UpdateTabHeader()
    {
        if (Parent is TabItem tab)
        {
            if (tab.Header is StackPanel panel && panel.Children.Count > 0 &&
                panel.Children[0] is TextBlock headerText)
            {
                headerText.Text = GetDisplayName();
            }
        }
    }

    private void WrapToggleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _wordWrap = !_wordWrap;
            EditorBox.TextWrapping = _wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            UpdateWrapButton();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] WrapToggleButton_Click FAILED: {ex.Message}");
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
            FileLog.Write($"[TextViewer] SaveButton_Click FAILED: {ex.Message}");
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

            if (!_isDirty)
            {
                _isDirty = true;
                UpdateSaveButton();
                UpdateTabHeader();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TextViewer] EditorBox_TextChanged FAILED: {ex.Message}");
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
            FileLog.Write($"[TextViewer] SaveCommand_Executed FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _isDirty;
    }
}
