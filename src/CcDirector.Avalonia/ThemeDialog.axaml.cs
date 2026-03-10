using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

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

    private void ThemeItem_Click(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control fe && fe.DataContext is ThemeEntry entry)
        {
            _selectedTheme = entry.Key;
            foreach (var t in _themes)
                t.IsSelected = t.Key == _selectedTheme;

            FileLog.Write($"[ThemeDialog] ThemeItem_Click: selected={_selectedTheme}");
        }
    }

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
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

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
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
            SwatchColor = new SolidColorBrush(Color.Parse(swatchHex));
            IsCurrent = key == currentTheme;
            IsSelected = key == currentTheme;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public ISolidColorBrush SwatchColor { get; }
        public bool IsCurrent { get; }

        public IBrush BorderBrushColor => IsSelected
            ? new SolidColorBrush(Color.Parse("#007ACC"))
            : new SolidColorBrush(Color.Parse("#3C3C3C"));

        public Thickness BorderThicknessValue => IsSelected
            ? new Thickness(2)
            : new Thickness(1);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderBrushColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderThicknessValue)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
