using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class OutputStyleDialog : Window
{
    private static readonly string[] OutputStyles = { "concise", "verbose", "markdown" };

    public OutputStyleDialog()
    {
        FileLog.Write("[OutputStyleDialog] Constructor: initializing");
        InitializeComponent();

        StyleCombo.ItemsSource = OutputStyles;

        Loaded += async (_, _) =>
        {
            await LoadDataAsync();
        };
    }

    private async Task LoadDataAsync()
    {
        FileLog.Write("[OutputStyleDialog] LoadDataAsync: reading current output style");

        var currentStyle = await Task.Run(ReadCurrentStyle);
        StyleCombo.SelectedItem = currentStyle;

        FileLog.Write($"[OutputStyleDialog] LoadDataAsync: currentStyle={currentStyle}");
    }

    private static string ReadCurrentStyle()
    {
        FileLog.Write("[OutputStyleDialog] ReadCurrentStyle: reading ~/.claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude.json");

        if (!File.Exists(path)) return "concise";

        try
        {
            var text = File.ReadAllText(path);
            var json = JsonNode.Parse(text);
            return json?["preferredOutputStyle"]?.GetValue<string>() ?? "concise";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OutputStyleDialog] ReadCurrentStyle FAILED: {ex.Message}");
            return "concise";
        }
    }

    private async void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var selected = StyleCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;

        FileLog.Write($"[OutputStyleDialog] BtnOk_Click: applying style={selected}");

        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("claude", $"config set preferredOutputStyle {selected}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                FileLog.Write($"[OutputStyleDialog] BtnOk_Click: style applied successfully");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[OutputStyleDialog] BtnOk_Click FAILED: {ex.Message}");
            }
        });

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[OutputStyleDialog] BtnCancel_Click: cancelled");
        Close();
    }
}
