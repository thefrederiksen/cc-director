using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class McpServersDialog : Window
{
    private readonly McpConfigManager _configManager;
    private readonly string? _projectDir;
    private readonly ObservableCollection<string> _serverNames = new();
    private List<McpServerConfig> _servers = new();
    private string? _selectedOriginalName;
    private bool _suppressSelectionChanged;

    private static readonly string[] TransportTypes = ["stdio", "sse"];

    public McpServersDialog(McpConfigManager configManager, string? projectDir = null)
    {
        InitializeComponent();

        _configManager = configManager;
        _projectDir = projectDir;

        TransportCombo.ItemsSource = TransportTypes;
        TransportCombo.SelectedIndex = 0;
        ServerList.ItemsSource = _serverNames;

        var scopes = new List<string> { "Global" };
        if (!string.IsNullOrEmpty(_projectDir))
            scopes.Add($"Project: {_projectDir}");
        ScopeCombo.ItemsSource = scopes;

        EditPanel.IsEnabled = false;

        Loaded += McpServersDialog_Loaded;
    }

    private void McpServersDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] Loaded");
            ScopeCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] Loaded FAILED: {ex.Message}");
            MessageBox.Show($"Failed to load MCP configuration: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -- Scope --

    private string GetCurrentConfigPath()
    {
        var scope = ScopeCombo.SelectedItem as string ?? "Global";
        if (scope.StartsWith("Project:") && _projectDir != null)
            return Path.Combine(_projectDir, ".mcp.json");
        return McpConfigManager.GlobalPath;
    }

    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            FileLog.Write($"[McpServersDialog] ScopeCombo_SelectionChanged: {ScopeCombo.SelectedItem}");
            LoadServerList();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] ScopeCombo_SelectionChanged FAILED: {ex.Message}");
            MessageBox.Show($"Failed to load servers: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -- Load --

    private void LoadServerList()
    {
        FileLog.Write("[McpServersDialog] LoadServerList");

        var configPath = GetCurrentConfigPath();
        _servers = _configManager.LoadServers(configPath);

        _suppressSelectionChanged = true;
        _serverNames.Clear();
        foreach (var server in _servers)
            _serverNames.Add(server.Name);
        _suppressSelectionChanged = false;

        if (_serverNames.Count > 0)
            ServerList.SelectedIndex = 0;
        else
            ClearEditForm();

        StatusText.Text = "";
    }

    // -- Selection --

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        try
        {
            var index = ServerList.SelectedIndex;
            if (index < 0 || index >= _servers.Count)
            {
                ClearEditForm();
                return;
            }

            var server = _servers[index];
            _selectedOriginalName = server.Name;
            FileLog.Write($"[McpServersDialog] ServerList_SelectionChanged: {server.Name}");

            EditPanel.IsEnabled = true;
            NameInput.Text = server.Name;
            CommandInput.Text = server.Command;
            UrlInput.Text = server.Url ?? "";

            TransportCombo.SelectedItem = server.TransportType;
            UpdateTransportVisibility(server.TransportType);

            ArgsInput.Text = string.Join(Environment.NewLine, server.Args);

            var envLines = server.Env.Select(kvp => $"{kvp.Key}={kvp.Value}");
            EnvInput.Text = string.Join(Environment.NewLine, envLines);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] ServerList_SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void ClearEditForm()
    {
        EditPanel.IsEnabled = false;
        _selectedOriginalName = null;
        NameInput.Text = "";
        CommandInput.Text = "";
        UrlInput.Text = "";
        TransportCombo.SelectedIndex = 0;
        ArgsInput.Text = "";
        EnvInput.Text = "";
        UpdateTransportVisibility("stdio");
    }

    // -- Transport toggle --

    private void TransportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var transport = TransportCombo.SelectedItem as string ?? "stdio";
        UpdateTransportVisibility(transport);
    }

    private void UpdateTransportVisibility(string transport)
    {
        if (StdioPanel == null || SsePanel == null) return;

        if (transport == "sse")
        {
            StdioPanel.Visibility = Visibility.Collapsed;
            SsePanel.Visibility = Visibility.Visible;
        }
        else
        {
            StdioPanel.Visibility = Visibility.Visible;
            SsePanel.Visibility = Visibility.Collapsed;
        }
    }

    // -- Add / Remove --

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnAdd_Click");

            var baseName = "new-server";
            var name = baseName;
            var counter = 1;
            while (_servers.Any(s => s.Name == name))
            {
                name = $"{baseName}-{counter}";
                counter++;
            }

            var entry = new McpServerConfig
            {
                Name = name,
                Command = "npx",
                TransportType = "stdio"
            };

            _servers.Add(entry);
            _serverNames.Add(entry.Name);
            ServerList.SelectedIndex = _serverNames.Count - 1;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnAdd_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to add server: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var index = ServerList.SelectedIndex;
            if (index < 0 || index >= _servers.Count) return;

            var name = _servers[index].Name;
            FileLog.Write($"[McpServersDialog] BtnRemove_Click: {name}");

            var result = MessageBox.Show(
                $"Remove server '{name}'?",
                "Confirm Remove",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            _servers.RemoveAt(index);
            _serverNames.RemoveAt(index);

            if (_serverNames.Count > 0)
                ServerList.SelectedIndex = Math.Min(index, _serverNames.Count - 1);
            else
                ClearEditForm();

            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnRemove_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to remove server: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -- Import from Claude Desktop --

    private void BtnImportDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnImportDesktop_Click");

            if (!File.Exists(McpConfigManager.DesktopPath))
            {
                MessageBox.Show(
                    $"Claude Desktop config not found at:\n{McpConfigManager.DesktopPath}",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var imported = _configManager.ImportFromClaudeDesktop();
            if (imported.Count == 0)
            {
                MessageBox.Show("No MCP servers found in Claude Desktop config.",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var addedCount = 0;
            foreach (var server in imported)
            {
                if (_servers.Any(s => s.Name == server.Name))
                {
                    FileLog.Write($"[McpServersDialog] Import: skipping duplicate '{server.Name}'");
                    continue;
                }

                _servers.Add(server);
                _serverNames.Add(server.Name);
                addedCount++;
            }

            if (addedCount > 0 && ServerList.SelectedIndex < 0)
                ServerList.SelectedIndex = 0;

            var msg = addedCount > 0
                ? $"Imported {addedCount} server(s). Click Save to persist."
                : "All servers already exist (skipped duplicates).";
            MessageBox.Show(msg, "Import", MessageBoxButton.OK, MessageBoxImage.Information);

            FileLog.Write($"[McpServersDialog] Imported {addedCount} of {imported.Count} servers");
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnImportDesktop_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to import: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -- Save --

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnSave_Click");

            // Apply current form edits to the selected server before saving
            ApplyFormToSelectedServer();

            // Validate: no duplicate names
            var names = _servers.Select(s => s.Name).ToList();
            var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                MessageBox.Show(
                    $"Duplicate server names: {string.Join(", ", duplicates)}",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate: no empty names
            if (_servers.Any(s => string.IsNullOrWhiteSpace(s.Name)))
            {
                MessageBox.Show("Server name cannot be empty.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var configPath = GetCurrentConfigPath();
            _configManager.SaveServers(configPath, _servers);

            StatusText.Text = "Saved";
            FileLog.Write("[McpServersDialog] BtnSave_Click: complete");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnSave_Click FAILED: {ex.Message}");
            MessageBox.Show($"Failed to save: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFormToSelectedServer()
    {
        var index = ServerList.SelectedIndex;
        if (index < 0 || index >= _servers.Count) return;

        var server = _servers[index];
        var newName = NameInput.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        server.Name = newName;
        server.TransportType = TransportCombo.SelectedItem as string ?? "stdio";
        server.Command = CommandInput.Text.Trim();
        server.Url = string.IsNullOrWhiteSpace(UrlInput.Text) ? null : UrlInput.Text.Trim();

        // Parse args (one per line, skip empty)
        server.Args = ArgsInput.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();

        // Parse env (KEY=VALUE per line)
        server.Env.Clear();
        var envLines = EnvInput.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        foreach (var line in envLines)
        {
            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0) continue;
            var key = line.Substring(0, eqIndex).Trim();
            var value = line.Substring(eqIndex + 1).Trim();
            if (key.Length > 0)
                server.Env[key] = value;
        }

        // Update the display name in the list
        _suppressSelectionChanged = true;
        _serverNames[index] = newName;
        _suppressSelectionChanged = false;

        FileLog.Write($"[McpServersDialog] ApplyFormToSelectedServer: {server.Name}");
    }

    // -- Close --

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[McpServersDialog] BtnClose_Click");
        Close();
    }
}
