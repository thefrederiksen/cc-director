using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Read-only settings view showing all configuration values grouped by category.
/// </summary>
public partial class SettingsView : UserControl
{
    // Frozen brushes
    private static readonly SolidColorBrush CategoryHeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));
    private static readonly SolidColorBrush CardBackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));
    private static readonly SolidColorBrush NameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));
    private static readonly SolidColorBrush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly SolidColorBrush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));
    private static readonly SolidColorBrush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsView] Loaded");
        AlphaModeToggle.IsChecked = AlphaMode.IsEnabled;
        _ = LoadSettingsAsync();
    }

    private void AlphaModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AlphaModeToggle.IsChecked == true;
        FileLog.Write($"[SettingsView] AlphaModeToggle_Click: enabled={enabled}");
        AlphaMode.SetEnabled(enabled);
    }

    public async Task LoadSettingsAsync()
    {
        FileLog.Write("[SettingsView] LoadSettingsAsync");
        AlphaModeToggle.IsChecked = AlphaMode.IsEnabled;
        LoadingText.Visibility = Visibility.Visible;
        ContentScroller.Visibility = Visibility.Collapsed;

        var categories = await Task.Run(BuildCategories);

        CategoriesPanel.Children.Clear();

        foreach (var category in categories)
        {
            AddCategoryToPanel(category);
        }

        LoadingText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        FileLog.Write($"[SettingsView] Loaded {categories.Count} categories");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsView] BtnRefresh_Click");
        await LoadSettingsAsync();
    }

    // --- Data Loading ---

    private static List<SettingCategory> BuildCategories()
    {
        FileLog.Write("[SettingsView] BuildCategories");
        var alpha = AlphaMode.IsEnabled;
        var categories = new List<SettingCategory>();

        // RELEASE categories
        categories.Add(BuildStoragePaths(alpha));
        categories.Add(BuildApplicationSettings());
        categories.Add(BuildRegistryFiles(alpha));
        categories.Add(BuildEnvironmentVariables());

        // ALPHA categories
        if (alpha)
        {
            categories.Add(BuildLlmSettings());
            categories.Add(BuildTerminalSettings());
            categories.Add(BuildScreenshotSettings());
            categories.Add(BuildCommunicationSettings());
        }

        return categories;
    }

    private static SettingCategory BuildStoragePaths(bool alpha)
    {
        var settings = new List<SettingItem>
        {
            new() { Name = "Root Directory", Value = CcStorage.Root(), SourceFile = "CcStorage (computed)" },
            new() { Name = "Config", Value = CcStorage.Config(), SourceFile = "CcStorage (computed)" },
            new() { Name = "Output", Value = CcStorage.Output(), SourceFile = "CcStorage (computed)" },
            new() { Name = "Logs", Value = CcStorage.Logs(), SourceFile = "CcStorage (computed)" },
        };

        if (alpha)
        {
            settings.Add(new() { Name = "Vault", Value = CcStorage.Vault(), SourceFile = "CcStorage (computed)" });
            settings.Add(new() { Name = "Bin", Value = CcStorage.Bin(), SourceFile = "CcStorage (computed)" });
            settings.Add(new() { Name = "Connections", Value = CcStorage.Connections(), SourceFile = "CcStorage (computed)" });
        }

        return new SettingCategory { Name = "Storage Paths", Settings = settings };
    }

    private static SettingCategory BuildApplicationSettings()
    {
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var settings = new List<SettingItem>();

        if (File.Exists(appSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(appSettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                settings.Add(ReadJsonSetting(root, "ClaudePath", appSettingsPath));
                settings.Add(ReadJsonSetting(root, "DefaultBufferSizeBytes", appSettingsPath));
                settings.Add(ReadJsonSetting(root, "GracefulShutdownTimeoutSeconds", appSettingsPath));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] BuildApplicationSettings FAILED: {ex.Message}");
                settings.Add(new SettingItem { Name = "Error", Value = ex.Message, SourceFile = appSettingsPath });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = "appsettings.json", Value = "(not found)", SourceFile = appSettingsPath });
        }

        return new SettingCategory { Name = "Application", Settings = settings };
    }

    private static SettingCategory BuildLlmSettings()
    {
        var configPath = CcStorage.ConfigJson();
        var settings = new List<SettingItem>();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("llm", out var llm))
                {
                    settings.Add(ReadJsonSetting(llm, "default_provider", configPath));

                    if (llm.TryGetProperty("openai", out var openai))
                    {
                        settings.Add(ReadJsonSetting(openai, "default_model", configPath, "openai.default_model"));
                        settings.Add(ReadJsonSetting(openai, "vision_model", configPath, "openai.vision_model"));
                    }

                    if (llm.TryGetProperty("claude_code", out var claude))
                    {
                        settings.Add(ReadJsonSetting(claude, "enabled", configPath, "claude_code.enabled"));
                    }
                }
                else
                {
                    settings.Add(new SettingItem { Name = "llm", Value = "(section not found)", SourceFile = configPath });
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] BuildLlmSettings FAILED: {ex.Message}");
                settings.Add(new SettingItem { Name = "Error", Value = ex.Message, SourceFile = configPath });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = "config.json", Value = "(not found)", SourceFile = configPath });
        }

        return new SettingCategory { Name = "LLM", Settings = settings };
    }

    private static SettingCategory BuildTerminalSettings()
    {
        var configPath = CcStorage.ConfigJson();
        var settings = new List<SettingItem>();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("terminal", out var terminal))
                {
                    settings.Add(ReadJsonSetting(terminal, "renderer", configPath, "terminal.renderer"));
                }
                else
                {
                    settings.Add(new SettingItem { Name = "terminal", Value = "(section not found)", SourceFile = configPath });
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] BuildTerminalSettings FAILED: {ex.Message}");
                settings.Add(new SettingItem { Name = "Error", Value = ex.Message, SourceFile = configPath });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = "config.json", Value = "(not found)", SourceFile = configPath });
        }

        return new SettingCategory { Name = "Terminal", Settings = settings };
    }

    private static SettingCategory BuildScreenshotSettings()
    {
        var configPath = CcStorage.ConfigJson();
        var settings = new List<SettingItem>();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("screenshots", out var screenshots))
                {
                    settings.Add(ReadJsonSetting(screenshots, "source_directory", configPath, "screenshots.source_directory"));
                }
                else
                {
                    settings.Add(new SettingItem { Name = "screenshots", Value = "(section not found)", SourceFile = configPath });
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] BuildScreenshotSettings FAILED: {ex.Message}");
                settings.Add(new SettingItem { Name = "Error", Value = ex.Message, SourceFile = configPath });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = "config.json", Value = "(not found)", SourceFile = configPath });
        }

        return new SettingCategory { Name = "Screenshots", Settings = settings };
    }

    private static SettingCategory BuildCommunicationSettings()
    {
        var configPath = CcStorage.ConfigJson();
        var settings = new List<SettingItem>();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("comm_manager", out var cm))
                {
                    settings.Add(ReadJsonSetting(cm, "default_persona", configPath, "comm_manager.default_persona"));

                    if (cm.TryGetProperty("send_from_accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
                    {
                        var accountList = new List<string>();
                        foreach (var account in accounts.EnumerateArray())
                        {
                            var name = account.TryGetProperty("name", out var n) ? n.GetString() : "";
                            var email = account.TryGetProperty("email", out var e) ? e.GetString() : "";
                            accountList.Add($"{name} <{email}>");
                        }
                        settings.Add(new SettingItem
                        {
                            Name = "send_from_accounts",
                            Value = string.Join("; ", accountList),
                            SourceFile = configPath
                        });
                    }
                }
                else
                {
                    settings.Add(new SettingItem { Name = "comm_manager", Value = "(section not found)", SourceFile = configPath });
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] BuildCommunicationSettings FAILED: {ex.Message}");
                settings.Add(new SettingItem { Name = "Error", Value = ex.Message, SourceFile = configPath });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = "config.json", Value = "(not found)", SourceFile = configPath });
        }

        return new SettingCategory { Name = "Communication", Settings = settings };
    }

    private static SettingCategory BuildRegistryFiles(bool alpha)
    {
        var settings = new List<SettingItem>();

        AddRegistryFile(settings, "Repositories", Path.Combine(CcStorage.Config(), "director", "repositories.json"));
        AddRegistryFile(settings, "Root Directories", Path.Combine(CcStorage.Config(), "director", "root-directories.json"));

        if (alpha)
        {
            AddRegistryFile(settings, "Claude Accounts", Path.Combine(CcStorage.Config(), "director", "claude-accounts.json"));
            AddRegistryFile(settings, "Browser Connections", CcStorage.ConnectionsRegistry());
        }

        return new SettingCategory { Name = "Registry Files", Settings = settings };
    }

    private static void AddRegistryFile(List<SettingItem> settings, string name, string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                int count = root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : 0;
                settings.Add(new SettingItem { Name = name, Value = $"{count} items", SourceFile = path });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsView] AddRegistryFile FAILED for {name}: {ex.Message}");
                settings.Add(new SettingItem { Name = name, Value = "(parse error)", SourceFile = path });
            }
        }
        else
        {
            settings.Add(new SettingItem { Name = name, Value = "(not found)", SourceFile = path });
        }
    }

    private static SettingCategory BuildEnvironmentVariables()
    {
        var settings = new List<SettingItem>();

        AddEnvVar(settings, "CC_DIRECTOR_ROOT");
        AddEnvVar(settings, "CC_VAULT_PATH");
        AddEnvVar(settings, "OPENAI_API_KEY", masked: true);
        AddEnvVar(settings, "LOCALAPPDATA");
        AddEnvVar(settings, "USERPROFILE");
        AddEnvVar(settings, "OneDrive");

        return new SettingCategory { Name = "Environment Variables", Settings = settings };
    }

    private static void AddEnvVar(List<SettingItem> settings, string name, bool masked = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        string displayValue;
        if (value == null)
            displayValue = "(not set)";
        else if (masked)
            displayValue = "***";
        else
            displayValue = value;

        settings.Add(new SettingItem { Name = name, Value = displayValue, SourceFile = "Environment variable" });
    }

    private static SettingItem ReadJsonSetting(JsonElement parent, string key, string sourcePath, string? displayName = null)
    {
        if (parent.TryGetProperty(key, out var value))
        {
            var strValue = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetRawText(),
                _ => value.GetRawText()
            };
            return new SettingItem { Name = displayName ?? key, Value = strValue, SourceFile = sourcePath };
        }

        return new SettingItem { Name = displayName ?? key, Value = "(not set)", SourceFile = sourcePath };
    }

    // --- UI Building ---

    private void AddCategoryToPanel(SettingCategory category)
    {
        // Category header
        var header = new TextBlock
        {
            Text = category.Name,
            Foreground = CategoryHeaderBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 16, 0, 6)
        };
        CategoriesPanel.Children.Add(header);

        // Card border
        var card = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8)
        };

        var stack = new StackPanel();

        for (int i = 0; i < category.Settings.Count; i++)
        {
            var setting = category.Settings[i];

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Name
            var nameBlock = new TextBlock
            {
                Text = setting.Name,
                Foreground = NameBrush,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 0);
            row.Children.Add(nameBlock);

            // Value
            var valueBlock = new TextBlock
            {
                Text = setting.Value,
                Foreground = ValueBrush,
                FontFamily = MonoFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = setting.Value
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(valueBlock);

            // Source (clickable if it's a file path)
            if (!string.IsNullOrEmpty(setting.SourceFile) && setting.SourceFile != "Environment variable" && setting.SourceFile != "CcStorage (computed)")
            {
                var link = new TextBlock
                {
                    Foreground = LinkBrush,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline,
                    ToolTip = setting.SourceFile
                };
                // Show just the filename
                link.Text = Path.GetFileName(setting.SourceFile);
                var filePath = setting.SourceFile;
                link.MouseLeftButtonDown += (_, _) => OpenFileInExplorer(filePath);
                Grid.SetColumn(link, 2);
                row.Children.Add(link);
            }
            else
            {
                var sourceBlock = new TextBlock
                {
                    Text = setting.SourceFile,
                    Foreground = DimBrush,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(sourceBlock, 2);
                row.Children.Add(sourceBlock);
            }

            stack.Children.Add(row);

            // Separator between rows (not after last)
            if (i < category.Settings.Count - 1)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = SeparatorBrush,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }

        card.Child = stack;
        CategoriesPanel.Children.Add(card);
    }

    private static void OpenFileInExplorer(string filePath)
    {
        FileLog.Write($"[SettingsView] OpenFileInExplorer: {filePath}");
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else if (Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(filePath)}\"");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsView] OpenFileInExplorer FAILED: {ex.Message}");
        }
    }
}

// --- Data Models ---

public class SettingItem
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string SourceFile { get; set; } = "";
}

public class SettingCategory
{
    public string Name { get; set; } = "";
    public List<SettingItem> Settings { get; set; } = new();
}
