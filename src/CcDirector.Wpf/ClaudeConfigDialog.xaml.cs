using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class ClaudeConfigDialog : Window
{
    private readonly string _claudeDir;
    private readonly string _claudeJsonPath;
    private readonly string _settingsJsonPath;
    private readonly string? _projectSettingsPath;
    private readonly string? _projectLocalSettingsPath;

    private readonly ObservableCollection<string> _allowedRules = new();
    private readonly ObservableCollection<string> _deniedRules = new();
    private readonly ObservableCollection<PluginEntry> _plugins = new();

    private static readonly string[] PermissionModes =
        ["plan", "acceptEdits", "auto", "bypassPermissions"];

    private static readonly string[] EffortLevels =
        ["", "low", "medium", "high"];

    public ClaudeConfigDialog(string? repoPath = null, string? initialTab = null)
    {
        InitializeComponent();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeDir = Path.Combine(home, ".claude");
        _claudeJsonPath = Path.Combine(home, ".claude.json");
        _settingsJsonPath = Path.Combine(_claudeDir, "settings.json");

        if (!string.IsNullOrEmpty(repoPath))
        {
            _projectSettingsPath = Path.Combine(repoPath, ".claude", "settings.json");
            _projectLocalSettingsPath = Path.Combine(repoPath, ".claude", "settings.local.json");
        }

        PermissionModeCombo.ItemsSource = PermissionModes;
        EffortLevelCombo.ItemsSource = EffortLevels;
        AllowedToolsList.ItemsSource = _allowedRules;
        DeniedToolsList.ItemsSource = _deniedRules;
        PluginsList.ItemsSource = _plugins;

        Loaded += (_, _) =>
        {
            LoadConfig();

            if (initialTab != null)
            {
                var tabIndex = initialTab.ToLowerInvariant() switch
                {
                    "general" => 0,
                    "permissions" => 1,
                    "plugins" => 2,
                    "hooks" => 3,
                    "files" => 4,
                    _ => 0
                };
                ConfigTabs.SelectedIndex = tabIndex;
                FileLog.Write($"[ClaudeConfigDialog] Initial tab: {initialTab} -> index {tabIndex}");
            }
        };
    }

    // ── Load ────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        FileLog.Write("[ClaudeConfigDialog] LoadConfig: reading configuration files");

        var claudeJson = ReadJsonFile(_claudeJsonPath);
        var settingsJson = ReadJsonFile(_settingsJsonPath);

        LoadGeneralTab(claudeJson, settingsJson);
        LoadPermissionsTab(settingsJson);
        LoadPluginsTab(settingsJson);
        LoadHooksTab(settingsJson);
        LoadFilesTab();

        SaveStatusText.Text = "";
    }

    private void LoadGeneralTab(JsonNode? claudeJson, JsonNode? settingsJson)
    {
        // Permission mode
        var mode = settingsJson?["permissions"]?["defaultMode"]?.GetValue<string>();
        PermissionModeCombo.SelectedItem = mode ?? "plan";

        // Model override (stored in env)
        var model = settingsJson?["env"]?["ANTHROPIC_MODEL"]?.GetValue<string>();
        ModelOverrideInput.Text = model ?? "";

        // Effort level
        var effort = settingsJson?["env"]?["CLAUDE_CODE_EFFORT_LEVEL"]?.GetValue<string>();
        EffortLevelCombo.SelectedItem = effort ?? "";

        // Max output tokens
        var tokens = settingsJson?["env"]?["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]?.GetValue<string>();
        MaxTokensInput.Text = tokens ?? "";

        // Bash timeout
        var timeout = settingsJson?["env"]?["BASH_DEFAULT_TIMEOUT_MS"]?.GetValue<string>();
        BashTimeoutInput.Text = timeout ?? "";

        // Auto-updates
        var autoUpdates = claudeJson?["autoUpdates"];
        AutoUpdatesCheck.IsChecked = autoUpdates != null && autoUpdates.GetValue<bool>();
    }

    private void LoadPermissionsTab(JsonNode? settingsJson)
    {
        _allowedRules.Clear();
        _deniedRules.Clear();

        if (settingsJson?["permissions"]?["allow"] is JsonArray allowArr)
        {
            foreach (var item in allowArr)
            {
                var val = item?.GetValue<string>();
                if (val != null) _allowedRules.Add(val);
            }
        }

        if (settingsJson?["permissions"]?["deny"] is JsonArray denyArr)
        {
            foreach (var item in denyArr)
            {
                var val = item?.GetValue<string>();
                if (val != null) _deniedRules.Add(val);
            }
        }
    }

    private void LoadPluginsTab(JsonNode? settingsJson)
    {
        _plugins.Clear();

        if (settingsJson?["enabledPlugins"] is JsonObject plugins)
        {
            foreach (var prop in plugins)
            {
                var isEnabled = prop.Value is JsonValue jv && jv.GetValue<bool>();
                var displayName = prop.Key.Split('@')[0];
                _plugins.Add(new PluginEntry
                {
                    FullKey = prop.Key,
                    DisplayName = displayName,
                    IsEnabled = isEnabled,
                });
            }
        }

        NoPluginsText.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadHooksTab(JsonNode? settingsJson)
    {
        var hookEntries = new List<HookEntry>();

        if (settingsJson?["hooks"] is JsonObject hooks)
        {
            foreach (var eventProp in hooks)
            {
                var commands = new List<string>();
                if (eventProp.Value is JsonArray groupArray)
                {
                    foreach (var group in groupArray)
                    {
                        if (group?["hooks"] is JsonArray innerHooks)
                        {
                            foreach (var hook in innerHooks)
                            {
                                var cmd = hook?["command"]?.GetValue<string>();
                                if (cmd == null) continue;

                                var isAsync = hook?["async"] is JsonValue av && av.GetValue<bool>();
                                if (isAsync) cmd += " (async)";
                                commands.Add(cmd);
                            }
                        }
                    }
                }

                if (commands.Count > 0)
                    hookEntries.Add(new HookEntry { EventName = eventProp.Key, Commands = commands });
            }
        }

        HooksList.ItemsSource = hookEntries.Count > 0 ? hookEntries : null;
        NoHooksText.Visibility = hookEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadFilesTab()
    {
        var files = new List<ConfigFileEntry>
        {
            new("User preferences (.claude.json)", _claudeJsonPath),
            new("User settings (permissions, hooks, plugins)", _settingsJsonPath),
        };

        if (_projectSettingsPath != null)
            files.Add(new("Project settings (shared)", _projectSettingsPath));

        if (_projectLocalSettingsPath != null)
            files.Add(new("Project settings (local)", _projectLocalSettingsPath));

        FilesList.ItemsSource = files;
    }

    // ── Save ────────────────────────────────────────────────────────

    private void SaveConfig()
    {
        FileLog.Write("[ClaudeConfigDialog] SaveConfig: writing configuration files");

        SaveSettingsJson();
        SaveClaudeJson();

        SaveStatusText.Text = "Saved";
        FileLog.Write("[ClaudeConfigDialog] SaveConfig: complete");
    }

    private void SaveSettingsJson()
    {
        // Read existing file to preserve fields we don't edit (hooks, schema, etc.)
        JsonNode? root = ReadJsonFile(_settingsJsonPath);
        var obj = root as JsonObject ?? new JsonObject();

        // Ensure $schema is present
        if (obj["$schema"] == null)
            obj["$schema"] = "https://json.schemastore.org/claude-code-settings.json";

        // Permissions
        var perms = obj["permissions"] as JsonObject ?? new JsonObject();
        obj["permissions"] = perms;

        var selectedMode = PermissionModeCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(selectedMode))
            perms["defaultMode"] = selectedMode;
        else
            perms.Remove("defaultMode");

        perms["allow"] = new JsonArray(_allowedRules.Select(r => JsonValue.Create(r)).ToArray());
        perms["deny"] = new JsonArray(_deniedRules.Select(r => JsonValue.Create(r)).ToArray());

        // Environment variables
        var env = obj["env"] as JsonObject ?? new JsonObject();
        obj["env"] = env;

        SetOrRemoveEnv(env, "ANTHROPIC_MODEL", ModelOverrideInput.Text);
        SetOrRemoveEnv(env, "CLAUDE_CODE_EFFORT_LEVEL", EffortLevelCombo.SelectedItem as string);
        SetOrRemoveEnv(env, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", MaxTokensInput.Text);
        SetOrRemoveEnv(env, "BASH_DEFAULT_TIMEOUT_MS", BashTimeoutInput.Text);

        // Remove env object entirely if empty
        if (env.Count == 0)
            obj.Remove("env");

        // Plugins
        var pluginsObj = new JsonObject();
        foreach (var p in _plugins)
            pluginsObj[p.FullKey] = p.IsEnabled;
        obj["enabledPlugins"] = pluginsObj;

        // Write
        WriteJsonFile(_settingsJsonPath, obj);
    }

    private void SaveClaudeJson()
    {
        JsonNode? root = ReadJsonFile(_claudeJsonPath);
        if (root is not JsonObject obj) return; // Don't create .claude.json if it doesn't exist

        obj["autoUpdates"] = AutoUpdatesCheck.IsChecked == true;

        WriteJsonFile(_claudeJsonPath, obj);
    }

    private static void SetOrRemoveEnv(JsonObject env, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            env[key] = value.Trim();
        else
            env.Remove(key);
    }

    // ── Permission Rules ────────────────────────────────────────────

    private void BtnAddAllow_Click(object sender, RoutedEventArgs e) => AddRule(_allowedRules, AddAllowInput);
    private void BtnRemoveAllow_Click(object sender, RoutedEventArgs e) => RemoveSelected(_allowedRules, AllowedToolsList);
    private void BtnAddDeny_Click(object sender, RoutedEventArgs e) => AddRule(_deniedRules, AddDenyInput);
    private void BtnRemoveDeny_Click(object sender, RoutedEventArgs e) => RemoveSelected(_deniedRules, DeniedToolsList);

    private void AddAllowInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddRule(_allowedRules, AddAllowInput); e.Handled = true; }
    }

    private void AddDenyInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddRule(_deniedRules, AddDenyInput); e.Handled = true; }
    }

    private static void AddRule(ObservableCollection<string> rules, TextBox input)
    {
        var text = input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!rules.Contains(text))
        {
            rules.Add(text);
            FileLog.Write($"[ClaudeConfigDialog] Added rule: {text}");
        }
        input.Clear();
        input.Focus();
    }

    private static void RemoveSelected(ObservableCollection<string> rules, ListBox list)
    {
        if (list.SelectedItem is string selected)
        {
            rules.Remove(selected);
            FileLog.Write($"[ClaudeConfigDialog] Removed rule: {selected}");
        }
    }

    // ── File Open ───────────────────────────────────────────────────

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var path = btn.Tag as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        FileLog.Write($"[ClaudeConfigDialog] Opening config file: {path}");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] Open file FAILED: {ex.Message}");
        }
    }

    // ── Button Handlers ─────────────────────────────────────────────

    private void BtnReload_Click(object sender, RoutedEventArgs e) => LoadConfig();
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveConfig();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ── JSON Helpers ────────────────────────────────────────────────

    private static JsonNode? ReadJsonFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] ReadJsonFile FAILED: {path} -> {ex.Message}");
            return null;
        }
    }

    private static void WriteJsonFile(string path, JsonNode node)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = node.ToJsonString(options);
        File.WriteAllText(path, json);
        FileLog.Write($"[ClaudeConfigDialog] WriteJsonFile: {path} ({json.Length} bytes)");
    }

    // ── Inner Types ─────────────────────────────────────────────────

    private class HookEntry
    {
        public string EventName { get; set; } = "";
        public List<string> Commands { get; set; } = new();
    }

    internal class PluginEntry : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public string FullKey { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private class ConfigFileEntry
    {
        public ConfigFileEntry(string label, string filePath)
        {
            Label = label;
            FilePath = filePath;
            Exists = File.Exists(filePath);
            StatusText = Exists ? "exists" : "not found";
            var color = Exists ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0x66, 0x66, 0x66);
            StatusBrush = new SolidColorBrush(color);
            StatusBrush.Freeze();
        }

        public string Label { get; }
        public string FilePath { get; }
        public bool Exists { get; }
        public string StatusText { get; }
        public Brush StatusBrush { get; }
    }
}
