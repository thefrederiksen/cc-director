using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

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

        LoadingText.Visibility = Visibility.Collapsed;

        if (entries != null && entries.Count > 0)
        {
            StatsGrid.ItemsSource = entries;
            StatsGrid.Visibility = Visibility.Visible;
            FileLog.Write($"[StatsDialog] LoadDataAsync: loaded {entries.Count} entries");
        }
        else
        {
            NoDataText.Visibility = Visibility.Visible;
            FileLog.Write("[StatsDialog] LoadDataAsync: no stats data found");
        }
    }

    private static List<dynamic>? ParseStatsFile()
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

            // Collect all unique field names across all date entries
            var allFields = new HashSet<string> { "Date" };
            var rawEntries = new List<(string Date, JsonObject Data)>();

            foreach (var prop in rootObj)
            {
                if (prop.Value is JsonObject dateObj)
                {
                    rawEntries.Add((prop.Key, dateObj));
                    foreach (var field in dateObj)
                        allFields.Add(field.Key);
                }
            }

            // Sort by date descending
            rawEntries.Sort((a, b) => string.Compare(b.Date, a.Date, StringComparison.Ordinal));

            // Build dynamic rows
            var entries = new List<dynamic>();
            foreach (var (date, data) in rawEntries)
            {
                dynamic row = new ExpandoObject();
                var dict = (IDictionary<string, object?>)row;
                dict["Date"] = date;

                foreach (var field in data)
                {
                    var value = field.Value;
                    if (value is JsonValue jv)
                    {
                        if (jv.TryGetValue<int>(out var intVal))
                            dict[field.Key] = intVal;
                        else if (jv.TryGetValue<long>(out var longVal))
                            dict[field.Key] = longVal;
                        else if (jv.TryGetValue<double>(out var dblVal))
                            dict[field.Key] = dblVal;
                        else
                            dict[field.Key] = jv.ToString();
                    }
                    else
                    {
                        dict[field.Key] = value?.ToJsonString() ?? "";
                    }
                }

                entries.Add(row);
            }

            return entries;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StatsDialog] ParseStatsFile FAILED: {ex.Message}");
            return null;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[StatsDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
