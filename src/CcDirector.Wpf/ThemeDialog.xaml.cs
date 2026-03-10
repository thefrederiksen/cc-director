using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class ThemeDialog : Window
{
    private readonly List<ThemeEntry> _themes = new();
    private string _selectedTheme = "dark";

    public ThemeDialog()
    {
        FileLog.Write("[ThemeDialog] Constructor: initializing");
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            await LoadDataAsync();
        };
    }

    private async Task LoadDataAsync()
    {
        FileLog.Write("[ThemeDialog] LoadDataAsync: reading current theme");

        var currentTheme = await Task.Run(ReadCurrentTheme);
        _selectedTheme = currentTheme;

        _themes.Clear();
        _themes.Add(new ThemeEntry("dark", "Dark", "#1E1E1E", currentTheme));
        _themes.Add(new ThemeEntry("light", "Light", "#FFFFFF", currentTheme));
        _themes.Add(new ThemeEntry("light-daltonized", "Light (Daltonized)", "#FFFFFF", currentTheme));
        _themes.Add(new ThemeEntry("dark-daltonized", "Dark (Daltonized)", "#1E1E1E", currentTheme));

        ThemeList.ItemsSource = _themes;

        FileLog.Write($"[ThemeDialog] LoadDataAsync: currentTheme={currentTheme}");
    }

    private static string ReadCurrentTheme()
    {
        FileLog.Write("[ThemeDialog] ReadCurrentTheme: reading ~/.claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude.json");

        if (!File.Exists(path)) return "dark";

        try
        {
            var text = File.ReadAllText(path);
            var json = JsonNode.Parse(text);
            return json?["theme"]?.GetValue<string>() ?? "dark";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ThemeDialog] ReadCurrentTheme FAILED: {ex.Message}");
            return "dark";
        }
    }

    private void ThemeItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ThemeEntry entry)
        {
            _selectedTheme = entry.Key;
            foreach (var t in _themes)
                t.IsSelected = t.Key == _selectedTheme;

            FileLog.Write($"[ThemeDialog] ThemeItem_Click: selected={_selectedTheme}");
        }
    }

    private async void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write($"[ThemeDialog] BtnOk_Click: applying theme={_selectedTheme}");

        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("claude", $"config set theme {_selectedTheme}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                FileLog.Write($"[ThemeDialog] BtnOk_Click: theme applied successfully");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ThemeDialog] BtnOk_Click FAILED: {ex.Message}");
            }
        });

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ThemeDialog] BtnCancel_Click: cancelled");
        Close();
    }

    internal class ThemeEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ThemeEntry(string key, string displayName, string swatchHex, string currentTheme)
        {
            Key = key;
            DisplayName = displayName;
            SwatchColor = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(swatchHex));
            SwatchColor.Freeze();
            IsCurrent = key == currentTheme;
            IsSelected = key == currentTheme;
            CurrentVisibility = IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public SolidColorBrush SwatchColor { get; }
        public bool IsCurrent { get; }
        public Visibility CurrentVisibility { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
