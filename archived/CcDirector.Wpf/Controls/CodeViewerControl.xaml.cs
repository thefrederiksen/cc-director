using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Utilities;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace CcDirector.Wpf.Controls;

public partial class CodeViewerControl : UserControl, IFileViewer
{
    private const int MaxFileSizeBytes = 512_000; // 500 KB

    private string? _filePath;
    private bool _isDirty;
    private bool _suppressTextChanged;
    private bool _wordWrap;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;

    /// <summary>
    /// Maps file extensions to AvalonEdit built-in highlighting definition names.
    /// </summary>
    private static readonly Dictionary<string, string> HighlightingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", "C#" },
        { ".py", "Python" },
        { ".js", "JavaScript" },
        { ".jsx", "JavaScript" },
        { ".ts", "JavaScript" },
        { ".tsx", "JavaScript" },
        { ".json", "Json" },
        { ".xml", "XML" },
        { ".xaml", "XML" },
        { ".csproj", "XML" },
        { ".fsproj", "XML" },
        { ".vbproj", "XML" },
        { ".props", "XML" },
        { ".targets", "XML" },
        { ".svg", "XML" },
        { ".html", "HTML" },
        { ".htm", "HTML" },
        { ".css", "CSS" },
        { ".sql", "TSQL" },
        { ".ps1", "PowerShell" },
        { ".java", "Java" },
        { ".cpp", "C++" },
        { ".c", "C++" },
        { ".h", "C++" },
        { ".hpp", "C++" },
        { ".php", "PHP" },
    };

    // Cached frozen brushes for dark theme
    private static readonly SolidColorBrush EditorBackground = FreezeBrush(0x1E, 0x1E, 0x1E);
    private static readonly SolidColorBrush EditorForeground = FreezeBrush(0xD4, 0xD4, 0xD4);
    private static readonly SolidColorBrush LineNumberBrush = FreezeBrush(0x85, 0x85, 0x85);
    private static readonly SolidColorBrush SelectionBrush = FreezeBrush(0x26, 0x4F, 0x78);
    private static readonly SolidColorBrush CurrentLineBg = FreezeAlphaBrush(0x30, 0x2A, 0x2D, 0x2E);
    private static readonly Pen CurrentLinePen = FreezePen(0x40, 0x3C, 0x3C, 0x3C);

    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FreezeAlphaBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(byte a, byte r, byte g, byte b)
    {
        var pen = new Pen(FreezeAlphaBrush(a, r, g, b), 1);
        pen.Freeze();
        return pen;
    }

    public CodeViewerControl()
    {
        InitializeComponent();
        ApplyDarkTheme();
        UpdateWrapButton();
        Editor.TextChanged += Editor_TextChanged;
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[CodeViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        LoadingText.Visibility = Visibility.Visible;
        Editor.Visibility = Visibility.Collapsed;

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
        Editor.Text = content;
        _suppressTextChanged = false;

        // Apply syntax highlighting based on file extension
        ApplyHighlighting(filePath);

        if (truncated)
        {
            Editor.IsReadOnly = true;
            FilePathText.Text = $"{filePath}  [TRUNCATED - file exceeds 500 KB]";
        }

        LoadingText.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        UpdateSaveButton();

        FileLog.Write($"[CodeViewer] LoadFileAsync complete: {filePath}, length={content.Length}, truncated={truncated}");
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

        FileLog.Write($"[CodeViewer] Save: {_filePath}");
        var text = Editor.Text;
        await Task.Run(() => File.WriteAllText(_filePath, text));
        _isDirty = false;
        UpdateSaveButton();
        UpdateTabHeader();
        FileLog.Write($"[CodeViewer] Save complete: {_filePath}");
    }

    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        return _isDirty ? $"*{name}" : name;
    }

    private void ApplyHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (HighlightingMap.TryGetValue(ext, out var highlightingName))
        {
            var definition = HighlightingManager.Instance.GetDefinition(highlightingName);
            if (definition != null)
            {
                Editor.SyntaxHighlighting = definition;
                FileLog.Write($"[CodeViewer] Applied highlighting: {highlightingName} for {ext}");
                return;
            }
        }

        // Let AvalonEdit try to detect by extension
        var detected = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (detected != null)
        {
            Editor.SyntaxHighlighting = detected;
            FileLog.Write($"[CodeViewer] Auto-detected highlighting: {detected.Name} for {ext}");
            return;
        }

        FileLog.Write($"[CodeViewer] No highlighting found for {ext}");
    }

    private void ApplyDarkTheme()
    {
        Editor.Background = EditorBackground;
        Editor.Foreground = EditorForeground;
        Editor.LineNumbersForeground = LineNumberBrush;
        Editor.TextArea.SelectionBrush = SelectionBrush;
        Editor.TextArea.SelectionForeground = null; // keep syntax colors in selection
        Editor.TextArea.TextView.CurrentLineBackground = CurrentLineBg;
        Editor.TextArea.TextView.CurrentLineBorder = CurrentLinePen;
        Editor.TextArea.Caret.CaretBrush = EditorForeground;
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
            Editor.WordWrap = _wordWrap;
            UpdateWrapButton();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] WrapToggleButton_Click FAILED: {ex.Message}");
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
            FileLog.Write($"[CodeViewer] SaveButton_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
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
            FileLog.Write($"[CodeViewer] Editor_TextChanged FAILED: {ex.Message}");
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
            FileLog.Write($"[CodeViewer] SaveCommand_Executed FAILED: {ex.Message}");
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
            FileLog.Write($"[CodeViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }

    private void OpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[CodeViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] OpenDefault FAILED: {ex.Message}");
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
            FileLog.Write($"[CodeViewer] Opened 'Open with' dialog: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] OpenWith FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open file:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
