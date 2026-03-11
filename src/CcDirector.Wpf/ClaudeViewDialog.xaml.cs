using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class ClaudeViewDialog : Window
{
    private readonly string? _repoPath;
    private readonly ObservableCollection<ClaudeTreeNode> _rootNodes = new();

    public ClaudeViewDialog(string? repoPath = null)
    {
        InitializeComponent();
        _repoPath = repoPath;

        FileTree.ItemsSource = _rootNodes;

        Loaded += async (_, _) =>
        {
            try
            {
                await System.Threading.Tasks.Task.Run(() => BuildTree());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ClaudeViewDialog] BuildTree FAILED: {ex.Message}");
                MessageBox.Show(this, $"Failed to scan Claude locations:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    // -- Tree Building -------------------------------------------------------

    private void BuildTree()
    {
        FileLog.Write("[ClaudeViewDialog] BuildTree: scanning Claude locations");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalDir = Path.Combine(home, ".claude");
        var roamingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var nodes = new List<ClaudeTreeNode>();

        nodes.Add(BuildInstructionsCategory(home, globalDir));
        nodes.Add(BuildSettingsCategory(home, globalDir));
        nodes.Add(BuildSkillsCategory(globalDir));
        nodes.Add(BuildMcpDesktopCategory(roamingDir));
        nodes.Add(BuildCommandsCategory(globalDir));
        nodes.Add(BuildHooksCategory(globalDir));
        nodes.Add(BuildDataCategory(globalDir));
        nodes.Add(BuildProjectSessionsCategory(globalDir));

        Dispatcher.BeginInvoke(() =>
        {
            _rootNodes.Clear();
            foreach (var node in nodes)
                _rootNodes.Add(node);
        });

        FileLog.Write($"[ClaudeViewDialog] BuildTree: {nodes.Count} categories built");
    }

    private ClaudeTreeNode BuildInstructionsCategory(string home, string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Instructions", "I");

        var globalClaude = Path.Combine(globalDir, "CLAUDE.md");
        if (File.Exists(globalClaude))
            cat.Children.Add(ClaudeTreeNode.File("CLAUDE.md (Global)", globalClaude, AbbrevPath(globalClaude)));

        if (!string.IsNullOrEmpty(_repoPath))
        {
            var projectClaude = Path.Combine(_repoPath, "CLAUDE.md");
            if (File.Exists(projectClaude))
            {
                var dirName = Path.GetFileName(_repoPath);
                cat.Children.Add(ClaudeTreeNode.File(
                    $"CLAUDE.md ({dirName})", projectClaude, AbbrevPath(projectClaude)));
            }
        }

        // Memory files in projects dir
        var projectsDir = Path.Combine(globalDir, "projects");
        if (Directory.Exists(projectsDir))
        {
            foreach (var projDir in Directory.GetDirectories(projectsDir))
            {
                var memoryDir = Path.Combine(projDir, "memory");
                if (!Directory.Exists(memoryDir)) continue;
                var memoryMd = Path.Combine(memoryDir, "MEMORY.md");
                if (File.Exists(memoryMd))
                {
                    var projName = Path.GetFileName(projDir);
                    cat.Children.Add(ClaudeTreeNode.File(
                        $"MEMORY.md ({projName})", memoryMd, AbbrevPath(memoryMd)));
                }
            }
        }

        cat.Badge = cat.Children.Count.ToString();
        return cat;
    }

    private ClaudeTreeNode BuildSettingsCategory(string home, string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Settings", "S");

        var globalSettings = Path.Combine(globalDir, "settings.json");
        if (File.Exists(globalSettings))
            cat.Children.Add(ClaudeTreeNode.File("settings.json (Global)", globalSettings, AbbrevPath(globalSettings)));

        if (!string.IsNullOrEmpty(_repoPath))
        {
            var projSettings = Path.Combine(_repoPath, ".claude", "settings.json");
            if (File.Exists(projSettings))
                cat.Children.Add(ClaudeTreeNode.File("settings.json (Project)", projSettings, AbbrevPath(projSettings)));

            var projLocalSettings = Path.Combine(_repoPath, ".claude", "settings.local.json");
            if (File.Exists(projLocalSettings))
                cat.Children.Add(ClaudeTreeNode.File("settings.local.json", projLocalSettings, AbbrevPath(projLocalSettings)));
        }

        var claudeJson = Path.Combine(home, ".claude.json");
        if (File.Exists(claudeJson))
            cat.Children.Add(ClaudeTreeNode.File(".claude.json (User Config)", claudeJson, AbbrevPath(claudeJson)));

        cat.Badge = cat.Children.Count.ToString();
        return cat;
    }

    private ClaudeTreeNode BuildSkillsCategory(string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Skills", "K");
        var skillsDir = Path.Combine(globalDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            var dirs = Directory.GetDirectories(skillsDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                var skillMd = Path.Combine(dir, "skill.md");
                var exists = File.Exists(skillMd);
                var node = ClaudeTreeNode.Folder(name + "/", dir, exists ? "has skill.md" : "");
                // Add skill.md as child if it exists
                if (exists)
                    node.Children.Add(ClaudeTreeNode.File("skill.md", skillMd, FormatSize(skillMd)));
                // Add other files in the skill dir
                foreach (var file in Directory.GetFiles(dir))
                {
                    var fn = Path.GetFileName(file);
                    if (fn.Equals("skill.md", StringComparison.OrdinalIgnoreCase)) continue;
                    node.Children.Add(ClaudeTreeNode.File(fn, file, FormatSize(file)));
                }
                cat.Children.Add(node);
            }
            cat.Badge = dirs.Length.ToString();
        }
        else
        {
            cat.Badge = "0";
        }
        return cat;
    }

    private ClaudeTreeNode BuildMcpDesktopCategory(string roamingDir)
    {
        var cat = ClaudeTreeNode.Category("MCP / Desktop", "M");

        var desktopConfig = Path.Combine(roamingDir, "claude_desktop_config.json");
        if (File.Exists(desktopConfig))
            cat.Children.Add(ClaudeTreeNode.File("claude_desktop_config.json", desktopConfig, FormatSize(desktopConfig)));

        var config = Path.Combine(roamingDir, "config.json");
        if (File.Exists(config))
            cat.Children.Add(ClaudeTreeNode.File("config.json", config, FormatSize(config)));

        cat.Badge = cat.Children.Count.ToString();
        return cat;
    }

    private ClaudeTreeNode BuildCommandsCategory(string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Commands", "C");
        var commandsDir = Path.Combine(globalDir, "commands");
        if (Directory.Exists(commandsDir))
        {
            var files = Directory.GetFiles(commandsDir, "*.md");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                cat.Children.Add(ClaudeTreeNode.File(Path.GetFileName(file), file, FormatSize(file)));
            cat.Badge = files.Length.ToString();
        }
        else
        {
            cat.Badge = "0";
        }
        return cat;
    }

    private ClaudeTreeNode BuildHooksCategory(string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Hooks", "H");
        var hooksDir = Path.Combine(globalDir, "hooks");
        if (Directory.Exists(hooksDir))
        {
            var files = Directory.GetFiles(hooksDir);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                cat.Children.Add(ClaudeTreeNode.File(Path.GetFileName(file), file, FormatSize(file)));
            cat.Badge = files.Length.ToString();
        }
        else
        {
            cat.Badge = "0";
        }
        return cat;
    }

    private ClaudeTreeNode BuildDataCategory(string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Data", "D");

        var history = Path.Combine(globalDir, "history.jsonl");
        if (File.Exists(history))
            cat.Children.Add(ClaudeTreeNode.File("history.jsonl", history, FormatSize(history)));

        var plansDir = Path.Combine(globalDir, "plans");
        if (Directory.Exists(plansDir))
        {
            var count = Directory.GetFiles(plansDir).Length;
            cat.Children.Add(ClaudeTreeNode.Folder($"plans/ ({count} files)", plansDir, AbbrevPath(plansDir)));
        }

        var tasksDir = Path.Combine(globalDir, "tasks");
        if (Directory.Exists(tasksDir))
        {
            var count = Directory.GetDirectories(tasksDir).Length;
            cat.Children.Add(ClaudeTreeNode.Folder($"tasks/ ({count} dirs)", tasksDir, AbbrevPath(tasksDir)));
        }

        var creds = Path.Combine(globalDir, ".credentials.json");
        if (File.Exists(creds))
            cat.Children.Add(ClaudeTreeNode.File(".credentials.json", creds, FormatSize(creds)));

        cat.Badge = cat.Children.Count.ToString();
        return cat;
    }

    private ClaudeTreeNode BuildProjectSessionsCategory(string globalDir)
    {
        var cat = ClaudeTreeNode.Category("Project Sessions", "P");
        var projectsDir = Path.Combine(globalDir, "projects");
        if (Directory.Exists(projectsDir))
        {
            var dirs = Directory.GetDirectories(projectsDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                // Count session files (*.jsonl)
                var sessionFiles = Directory.GetFiles(dir, "*.jsonl");
                var desc = sessionFiles.Length > 0 ? $"{sessionFiles.Length} sessions" : "";
                cat.Children.Add(ClaudeTreeNode.Folder(name + "/", dir, desc));
            }
            cat.Badge = dirs.Length.ToString();
        }
        else
        {
            cat.Badge = "0";
        }
        return cat;
    }

    // -- Helpers --------------------------------------------------------------

    private static string AbbrevPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path.Substring(home.Length);
        return path;
    }

    private static string FormatSize(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return "";
            var bytes = info.Length;
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
        catch (IOException ex)
        {
            FileLog.Write($"[ClaudeViewDialog] FormatSize FAILED for {filePath}: {ex.Message}");
            return "";
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLog.Write($"[ClaudeViewDialog] FormatSize FAILED for {filePath}: {ex.Message}");
            return "";
        }
    }

    private ClaudeTreeNode? GetSelectedNode()
    {
        return FileTree.SelectedItem as ClaudeTreeNode;
    }

    private void OpenFileInEditor(string path)
    {
        FileLog.Write($"[ClaudeViewDialog] OpenFileInEditor: {path}");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeViewDialog] OpenFileInEditor FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to open file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolderInExplorer(string path)
    {
        FileLog.Write($"[ClaudeViewDialog] OpenFolderInExplorer: {path}");
        try
        {
            if (File.Exists(path))
            {
                // Select the file in Explorer
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeViewDialog] OpenFolderInExplorer FAILED: {ex.Message}");
        }
    }

    // -- Event Handlers -------------------------------------------------------

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var node = GetSelectedNode();
        if (node == null)
        {
            StatusPathText.Text = "Select a file to see its path";
            BtnEdit.IsEnabled = false;
            BtnOpenFolder.IsEnabled = false;
            return;
        }

        if (!string.IsNullOrEmpty(node.FullPath))
        {
            StatusPathText.Text = node.FullPath;
            BtnEdit.IsEnabled = !node.IsFolder && !node.IsCategory && File.Exists(node.FullPath);
            BtnOpenFolder.IsEnabled = true;
        }
        else
        {
            StatusPathText.Text = node.Name;
            BtnEdit.IsEnabled = false;
            BtnOpenFolder.IsEnabled = false;
        }
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = GetSelectedNode();
        if (node == null || string.IsNullOrEmpty(node.FullPath)) return;
        if (node.IsCategory) return;

        if (!node.IsFolder && File.Exists(node.FullPath))
        {
            OpenFileInEditor(node.FullPath);
            e.Handled = true;
        }
        else if (node.IsFolder && Directory.Exists(node.FullPath))
        {
            OpenFolderInExplorer(node.FullPath);
            e.Handled = true;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ClaudeViewDialog] BtnRefresh_Click");
        _ = System.Threading.Tasks.Task.Run(() => BuildTree());
    }

    private void BtnOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node != null && !string.IsNullOrEmpty(node.FullPath))
        {
            OpenFolderInExplorer(node.FullPath);
        }
        else
        {
            // Default: open ~/.claude
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeDir = Path.Combine(home, ".claude");
            if (Directory.Exists(claudeDir))
                OpenFolderInExplorer(claudeDir);
        }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node == null || string.IsNullOrEmpty(node.FullPath)) return;
        if (File.Exists(node.FullPath))
            OpenFileInEditor(node.FullPath);
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node == null || string.IsNullOrEmpty(node.FullPath)) return;
        OpenFolderInExplorer(node.FullPath);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}

// -- Tree Data Model ----------------------------------------------------------

public class ClaudeTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Badge { get; set; } = "";
    public bool IsFolder { get; set; }
    public bool IsCategory { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public ObservableCollection<ClaudeTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ClaudeTreeNode Category(string name, string icon) => new()
    {
        Name = name,
        Icon = icon,
        IsCategory = true,
        IsExpanded = true,
    };

    public static ClaudeTreeNode File(string name, string fullPath, string description = "") => new()
    {
        Name = name,
        FullPath = fullPath,
        Description = description,
        IsFolder = false,
        IsExpanded = false,
    };

    public static ClaudeTreeNode Folder(string name, string fullPath, string description = "") => new()
    {
        Name = name,
        FullPath = fullPath,
        Description = description,
        IsFolder = true,
        IsExpanded = false,
    };
}

// -- Converters ---------------------------------------------------------------

public class LevelToMarginConverter : IValueConverter
{
    public static readonly LevelToMarginConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = (int)value;
        return new Thickness(level * 16, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
