using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public class StatsEntryViewModel
{
    public string DisplayText { get; }

    public StatsEntryViewModel(string date, JsonObject data)
    {
        var parts = new List<string> { $"Date: {date}" };
        foreach (var field in data)
        {
            var val = field.Value is JsonValue jv ? jv.ToString() : field.Value?.ToJsonString() ?? "";
            parts.Add($"{field.Key}: {val}");
        }
        DisplayText = string.Join("  |  ", parts);
    }
}

public partial class StatsDialog : Window
{
    public StatsDialog()
    {
        FileLog.Write("[StatsDialog] Constructor: initializing");
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            await LoadDataAsync();
        };
    }

    private async Task LoadDataAsync()
    {
        FileLog.Write("[StatsDialog] LoadDataAsync: reading stats-cache.json");

        var entries = await Task.Run(ParseStatsFile);

        LoadingText.IsVisible = false;

        if (entries != null && entries.Count > 0)
        {
            StatsItemsControl.ItemsSource = entries;
            StatsItemsControl.IsVisible = true;
            FileLog.Write($"[StatsDialog] LoadDataAsync: loaded {entries.Count} entries");
        }
        else
        {
            NoDataText.IsVisible = true;
            FileLog.Write("[StatsDialog] LoadDataAsync: no stats data found");
        }
    }

    private static List<StatsEntryViewModel>? ParseStatsFile()
    {
        FileLog.Write("[StatsDialog] ParseStatsFile: reading file");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude", "stats-cache.json");

        if (!File.Exists(path))
        {
            FileLog.Write("[StatsDialog] ParseStatsFile: file not found");
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            var root = JsonNode.Parse(text);

            if (root is not JsonObject rootObj)
            {
                FileLog.Write("[StatsDialog] ParseStatsFile: root is not a JSON object");
                return null;
            }

            var entries = new List<StatsEntryViewModel>();
            var rawEntries = new List<(string Date, JsonObject Data)>();

            foreach (var prop in rootObj)
            {
                if (prop.Value is JsonObject dateObj)
                    rawEntries.Add((prop.Key, dateObj));
            }

            rawEntries.Sort((a, b) => string.Compare(b.Date, a.Date, StringComparison.Ordinal));

            foreach (var (date, data) in rawEntries)
                entries.Add(new StatsEntryViewModel(date, data));

            return entries;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StatsDialog] ParseStatsFile FAILED: {ex.Message}");
            return null;
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[StatsDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
