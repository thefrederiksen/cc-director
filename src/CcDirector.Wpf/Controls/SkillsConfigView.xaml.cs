using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Wpf.Helpers;

namespace CcDirector.Wpf.Controls;

public partial class SkillsConfigView : UserControl
{
    // Frozen brushes
    private static readonly Brush BrushGlobal = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush BrushGlobalBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x3A)));
    private static readonly Brush BrushProject = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush BrushProjectBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x2A)));
    private static readonly Brush BrushMemory = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly Brush BrushMemoryBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x1B, 0x3A)));
    private static readonly Brush BrushMcp = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush BrushMcpBg = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x1B)));
    private static readonly Brush BrushText = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly Brush BrushMuted = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Brush BrushSubtle = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
    private static readonly Brush BrushSelected = Freeze(new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)));
    private static readonly Brush BrushAccent = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));

    // Icon paths (inline SVG-style Path data)
    private const string IconDocument = "M14,2H6C4.9,2,4,2.9,4,4v16c0,1.1,0.9,2,2,2h12c1.1,0,2-0.9,2-2V8L14,2z M16,18H8v-2h8V18z M16,14H8v-2h8V14z M13,9V3.5L18.5,9H13z";
    private const string IconSkill = "M12,2L4,7v10l8,5l8-5V7L12,2z M12,4.3L18,8v1.5l-6,3.7L6,9.5V8L12,4.3z M6,11.3l5,3.1v5.3l-5-3.1V11.3z M13,19.7v-5.3l5-3.1v5.3L13,19.7z";
    private const string IconServer = "M4,1h16v6H4V1z M6,3v2h2V3H6z M10,3v2h8V3H10z M4,9h16v6H4V9z M6,11v2h2v-2H6z M10,11v2h8v-2H10z M4,17h16v6H4V17z M6,19v2h2v-2H6z M10,19v2h8v-2H10z";
    private const string IconSettings = "M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61 l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41 h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87 C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58 c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54 c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96 c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6 s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z";

    private readonly ClaudeConfigDiscovery _discovery = new();
    private ClaudeConfigTree? _tree;
    private string? _repoPath;
    private string? _currentFilePath;
    private string _rawContent = "";
    private bool _isDirty;
    private bool _isEditing;
    private bool _suppressTextChanged;
    private Border? _selectedTreeItem;

    // Currently selected MCP server (for remove action)
    private McpServerEntry? _selectedMcpServer;

    public SkillsConfigView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Loads the configuration tree for the given repo path.
    /// Called when the view becomes visible.
    /// </summary>
    public async Task LoadAsync(string? repoPath)
    {
        FileLog.Write($"[SkillsConfigView] LoadAsync: repoPath={repoPath ?? "(null)"}");
        _repoPath = repoPath;

        TreePanel.Children.Clear();
        TreePanel.Children.Add(new TextBlock
        {
            Text = "Loading...",
            Foreground = BrushMuted,
            FontSize = 12,
            Margin = new Thickness(12, 8, 0, 0)
        });

        var tree = await Task.Run(() => _discovery.Discover(repoPath));
        _tree = tree;

        RebuildTree("");
        FileLog.Write("[SkillsConfigView] LoadAsync complete");
    }

    /// <summary>
    /// Refreshes the tree with the last known repo path.
    /// </summary>
    public async Task RefreshAsync()
    {
        await LoadAsync(_repoPath);
    }

    private void RebuildTree(string filter)
    {
        TreePanel.Children.Clear();
        if (_tree == null) return;

        var lowerFilter = filter.ToLowerInvariant();

        // CLAUDE.md Files section
        var mdFiles = _tree.ClaudeMdFiles
            .Where(f => MatchesFilter(f.Label, f.FilePath, lowerFilter))
            .ToList();
        if (mdFiles.Count > 0)
        {
            AddSection(IconDocument, "CLAUDE.md Files", BrushText, mdFiles.Count, panel =>
            {
                foreach (var file in mdFiles)
                {
                    var isMemory = file.Label.StartsWith("Memory:");
                    var scopeBrush = isMemory ? BrushMemory : (file.Label == "Global" ? BrushGlobal : BrushProject);
                    var scopeBg = isMemory ? BrushMemoryBg : (file.Label == "Global" ? BrushGlobalBg : BrushProjectBg);
                    AddFileItem(panel, file.Label, file.Description, file.FilePath, scopeBrush, scopeBg);
                }
            });
        }

        // Global Skills section
        var globalSkills = _tree.GlobalSkills
            .Where(s => MatchesFilter(s.Name, s.Description, lowerFilter))
            .ToList();
        if (globalSkills.Count > 0)
        {
            AddSection(IconSkill, "Global Skills", BrushGlobal, globalSkills.Count, panel =>
            {
                foreach (var skill in globalSkills)
                    AddSkillItem(panel, skill, BrushGlobal, BrushGlobalBg);
            });
        }

        // Project Skills section
        var projectSkills = _tree.ProjectSkills
            .Where(s => MatchesFilter(s.Name, s.Description, lowerFilter))
            .ToList();
        if (projectSkills.Count > 0)
        {
            AddSection(IconSkill, "Project Skills", BrushProject, projectSkills.Count, panel =>
            {
                foreach (var skill in projectSkills)
                    AddSkillItem(panel, skill, BrushProject, BrushProjectBg);
            });
        }

        // MCP Servers section
        var mcpServers = _tree.McpServers
            .Where(s => MatchesFilter(s.Name, s.Command, lowerFilter))
            .ToList();
        if (mcpServers.Count > 0)
        {
            AddSection(IconServer, "MCP Servers", BrushMcp, mcpServers.Count, panel =>
            {
                foreach (var server in mcpServers)
                    AddMcpItem(panel, server);
            });
        }

        // Settings section
        var settings = _tree.SettingsFiles
            .Where(f => MatchesFilter(f.Label, f.FilePath, lowerFilter))
            .ToList();
        if (settings.Count > 0)
        {
            AddSection(IconSettings, "Settings", BrushMuted, settings.Count, panel =>
            {
                foreach (var file in settings)
                {
                    var scopeBrush = file.Label.Contains("Global") ? BrushGlobal : BrushProject;
                    var scopeBg = file.Label.Contains("Global") ? BrushGlobalBg : BrushProjectBg;
                    AddFileItem(panel, file.Label, file.Description, file.FilePath, scopeBrush, scopeBg);
                }
            });
        }

        // Empty state for filter
        if (TreePanel.Children.Count == 0)
        {
            TreePanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(filter) ? "No configuration found" : "No matches",
                Foreground = BrushSubtle,
                FontSize = 12,
                Margin = new Thickness(12, 16, 0, 0)
            });
        }
    }

    private void AddSection(string iconData, string title, Brush iconBrush, int count, Action<StackPanel> populateItems)
    {
        var contentPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };

        var toggle = new ToggleButton
        {
            IsChecked = true,
            Style = (Style)FindResource("SectionHeader")
        };

        // Header content
        var header = new DockPanel();

        // Icon
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconData),
            Fill = iconBrush,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        header.Children.Add(icon);

        // Title
        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = BrushText,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(titleBlock);

        // Count badge
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = count.ToString(),
                Foreground = BrushText,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };
        header.Children.Add(badge);

        toggle.Content = header;

        // Bind visibility of content to toggle state
        toggle.Checked += (_, _) => contentPanel.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) => contentPanel.Visibility = Visibility.Collapsed;

        populateItems(contentPanel);

        TreePanel.Children.Add(toggle);
        TreePanel.Children.Add(contentPanel);
    }

    private void AddFileItem(StackPanel parent, string label, string description, string filePath,
                              Brush scopeBrush, Brush scopeBg)
    {
        var border = new Border
        {
            Style = (Style)FindResource("TreeItem"),
            Margin = new Thickness(16, 0, 4, 0)
        };

        var stack = new StackPanel();

        // Top row: label + scope badge
        var topRow = new DockPanel();

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = BrushText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        topRow.Children.Add(labelText);

        var scopeBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = scopeBg,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label.Contains("Global") ? "global" : (label.StartsWith("Memory:") ? "memory" : "project"),
                Foreground = scopeBrush,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };
        topRow.Children.Add(scopeBadge);
        stack.Children.Add(topRow);

        // Description
        if (!string.IsNullOrEmpty(description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = BrushSubtle,
                FontSize = 10,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        border.Child = stack;
        border.Tag = filePath;
        border.MouseLeftButtonDown += TreeItem_Click;

        parent.Children.Add(border);
    }

    private void AddSkillItem(StackPanel parent, SkillEntry skill, Brush scopeBrush, Brush scopeBg)
    {
        var border = new Border
        {
            Style = (Style)FindResource("TreeItem"),
            Margin = new Thickness(16, 0, 4, 0)
        };

        var stack = new StackPanel();

        // Top row: slash + name + scope
        var topRow = new DockPanel();

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(new TextBlock
        {
            Text = "/",
            Foreground = BrushAccent,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        namePanel.Children.Add(new TextBlock
        {
            Text = skill.Name,
            Foreground = BrushText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        topRow.Children.Add(namePanel);

        // Scope badge
        var scopeBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = scopeBg,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = skill.Scope,
                Foreground = scopeBrush,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };
        topRow.Children.Add(scopeBadge);
        stack.Children.Add(topRow);

        // Description
        if (!string.IsNullOrEmpty(skill.Description))
        {
            var descText = skill.Description;
            if (descText.Length > 80) descText = descText.Substring(0, 77) + "...";
            stack.Children.Add(new TextBlock
            {
                Text = descText,
                Foreground = BrushSubtle,
                FontSize = 10,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        border.Child = stack;
        border.Tag = skill.FilePath;
        border.MouseLeftButtonDown += TreeItem_Click;

        parent.Children.Add(border);
    }

    private void AddMcpItem(StackPanel parent, McpServerEntry server)
    {
        var border = new Border
        {
            Style = (Style)FindResource("TreeItem"),
            Margin = new Thickness(16, 0, 4, 0)
        };

        var stack = new StackPanel();

        // Top row: name + scope
        var topRow = new DockPanel();

        topRow.Children.Add(new TextBlock
        {
            Text = server.Name,
            Foreground = BrushText,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var scopeBrush = server.Scope == "global" ? BrushGlobal : BrushProject;
        var scopeBg = server.Scope == "global" ? BrushGlobalBg : BrushProjectBg;
        var scopeBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = scopeBg,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = server.Scope,
                Foreground = scopeBrush,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };
        topRow.Children.Add(scopeBadge);
        stack.Children.Add(topRow);

        // Command preview
        var cmdText = server.Command;
        if (server.Args.Count > 0)
            cmdText += " " + string.Join(" ", server.Args.Take(3));
        if (cmdText.Length > 60) cmdText = cmdText.Substring(0, 57) + "...";

        stack.Children.Add(new TextBlock
        {
            Text = cmdText,
            Foreground = BrushMuted,
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        border.Child = stack;
        border.Tag = server; // Store the McpServerEntry as tag
        border.MouseLeftButtonDown += McpItem_Click;

        parent.Children.Add(border);
    }

    // --- Event handlers ---

    private void TreeItem_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border border || border.Tag is not string filePath)
                return;

            FileLog.Write($"[SkillsConfigView] TreeItem_Click: {filePath}");
            SelectTreeItem(border);
            _selectedMcpServer = null;
            McpDetailBar.Visibility = Visibility.Collapsed;
            _ = LoadFileContent(filePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] TreeItem_Click FAILED: {ex.Message}");
        }
    }

    private void McpItem_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border border || border.Tag is not McpServerEntry server)
                return;

            FileLog.Write($"[SkillsConfigView] McpItem_Click: {server.Name}");
            SelectTreeItem(border);
            _selectedMcpServer = server;

            // Show MCP detail bar
            McpDetailBar.Visibility = Visibility.Visible;
            McpCommandText.Text = server.Command;
            McpArgsText.Text = server.Args.Count > 0 ? string.Join(" ", server.Args) : "(none)";

            // Load the config file content
            _ = LoadFileContent(server.ConfigPath, server.Name);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] McpItem_Click FAILED: {ex.Message}");
        }
    }

    private void SelectTreeItem(Border border)
    {
        // Deselect previous
        if (_selectedTreeItem != null)
            _selectedTreeItem.Background = Brushes.Transparent;

        // Select new
        _selectedTreeItem = border;
        border.Background = BrushSelected;
    }

    private async Task LoadFileContent(string filePath, string? displayTitle = null)
    {
        FileLog.Write($"[SkillsConfigView] LoadFileContent: {filePath}");

        _currentFilePath = filePath;
        _isDirty = false;
        _isEditing = false;
        BtnSave.Visibility = Visibility.Collapsed;
        BtnToggleEdit.Content = "Edit";

        // Set title and path
        ContentTitle.Text = displayTitle ?? Path.GetFileName(filePath);
        ContentPath.Text = filePath;

        // Scope badge
        UpdateScopeBadge(filePath);

        // Show content panel
        EmptyState.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        // Load file
        try
        {
            var content = await Task.Run(() => File.ReadAllText(filePath));
            _rawContent = content;

            // Show preview for .md, raw for .json
            if (filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                RenderPreview();
            }
            else
            {
                // JSON or other: show in editor mode directly (read-only preview)
                ShowEditorReadOnly();
            }

            FileLog.Write($"[SkillsConfigView] LoadFileContent complete: {filePath}, length={content.Length}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] LoadFileContent FAILED: {ex.Message}");
            PreviewViewer.Visibility = Visibility.Collapsed;
            EditorBox.Visibility = Visibility.Visible;
            _suppressTextChanged = true;
            EditorBox.Text = $"Failed to load: {ex.Message}";
            EditorBox.IsReadOnly = true;
            _suppressTextChanged = false;
        }
    }

    private void UpdateScopeBadge(string filePath)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isGlobal = filePath.StartsWith(homeDir, StringComparison.OrdinalIgnoreCase);
        var isMemory = filePath.Contains("memory", StringComparison.OrdinalIgnoreCase);

        if (isMemory)
        {
            ContentScopeBadge.Background = BrushMemoryBg;
            ContentScopeText.Text = "memory";
            ContentScopeText.Foreground = BrushMemory;
        }
        else if (isGlobal)
        {
            ContentScopeBadge.Background = BrushGlobalBg;
            ContentScopeText.Text = "global";
            ContentScopeText.Foreground = BrushGlobal;
        }
        else
        {
            ContentScopeBadge.Background = BrushProjectBg;
            ContentScopeText.Text = "project";
            ContentScopeText.Foreground = BrushProject;
        }
        ContentScopeBadge.Visibility = Visibility.Visible;
    }

    private void RenderPreview()
    {
        _isEditing = false;
        var doc = MarkdownFlowDocumentRenderer.Render(_rawContent);
        PreviewViewer.Document = doc;
        PreviewViewer.Visibility = Visibility.Visible;
        EditorBox.Visibility = Visibility.Collapsed;
        BtnToggleEdit.Content = "Edit";
    }

    private void ShowEditorReadOnly()
    {
        _isEditing = false;
        _suppressTextChanged = true;
        EditorBox.Text = _rawContent;
        EditorBox.IsReadOnly = true;
        _suppressTextChanged = false;
        EditorBox.Visibility = Visibility.Visible;
        PreviewViewer.Visibility = Visibility.Collapsed;
        BtnToggleEdit.Content = "Edit";
    }

    private void ShowEditor()
    {
        _isEditing = true;
        _suppressTextChanged = true;
        EditorBox.Text = _rawContent;
        EditorBox.IsReadOnly = false;
        _suppressTextChanged = false;
        EditorBox.Visibility = Visibility.Visible;
        PreviewViewer.Visibility = Visibility.Collapsed;
        BtnToggleEdit.Content = "Preview";
        EditorBox.Focus();
    }

    private async Task SaveCurrentFile()
    {
        if (_currentFilePath == null || !_isDirty) return;

        FileLog.Write($"[SkillsConfigView] SaveCurrentFile: {_currentFilePath}");
        await Task.Run(() => File.WriteAllText(_currentFilePath, _rawContent));
        _isDirty = false;
        BtnSave.Visibility = Visibility.Collapsed;
        FileLog.Write($"[SkillsConfigView] SaveCurrentFile complete: {_currentFilePath}");
    }

    // --- Button handlers ---

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SkillsConfigView] BtnRefresh_Click");
        _ = RefreshAsync();
    }

    private void BtnToggleEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isEditing)
            {
                // Switch to preview
                _rawContent = EditorBox.Text;
                if (_currentFilePath != null && _currentFilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    RenderPreview();
                else
                    ShowEditorReadOnly();
            }
            else
            {
                ShowEditor();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] BtnToggleEdit_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentFile();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] BtnSave_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;
            Process.Start(new ProcessStartInfo(_currentFilePath) { UseShellExecute = true });
            FileLog.Write($"[SkillsConfigView] Opened externally: {_currentFilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] BtnOpenExternal_Click FAILED: {ex.Message}");
        }
    }

    private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;
            Clipboard.SetText(_currentFilePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] BtnCopyPath_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnRemoveMcp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedMcpServer == null) return;

            var result = MessageBox.Show(
                $"Remove MCP server '{_selectedMcpServer.Name}' from {_selectedMcpServer.Scope} config?\n\nConfig file: {_selectedMcpServer.ConfigPath}",
                "Remove MCP Server",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            FileLog.Write($"[SkillsConfigView] BtnRemoveMcp_Click: removing {_selectedMcpServer.Name}");

            var success = await Task.Run(() =>
                _discovery.RemoveMcpServer(_selectedMcpServer.ConfigPath, _selectedMcpServer.Name));

            if (success)
            {
                _selectedMcpServer = null;
                McpDetailBar.Visibility = Visibility.Collapsed;
                ContentPanel.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                await RefreshAsync();
            }
            else
            {
                MessageBox.Show("Failed to remove MCP server. Check the log for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] BtnRemoveMcp_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to remove MCP server: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            var text = SearchBox.Text;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Visible : Visibility.Collapsed;
            RebuildTree(text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] SearchBox_TextChanged FAILED: {ex.Message}");
        }
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (_suppressTextChanged) return;
            _rawContent = EditorBox.Text;
            if (!_isDirty)
            {
                _isDirty = true;
                BtnSave.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] EditorBox_TextChanged FAILED: {ex.Message}");
        }
    }

    private async void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            await SaveCurrentFile();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillsConfigView] SaveCommand_Executed FAILED: {ex.Message}");
        }
    }

    private void SaveCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _isDirty;
    }

    // --- Helpers ---

    private static bool MatchesFilter(string primary, string secondary, string lowerFilter)
    {
        if (string.IsNullOrEmpty(lowerFilter)) return true;
        return primary.ToLowerInvariant().Contains(lowerFilter) ||
               secondary.ToLowerInvariant().Contains(lowerFilter);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
