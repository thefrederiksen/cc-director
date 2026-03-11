using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowRunsDialog : Window
{
    private readonly WorkflowStore _store;
    private readonly string _connection;
    private readonly string _workflowName;
    private readonly List<RunEntry> _runEntries = new();

    public WorkflowRunsDialog(WorkflowStore store, string connection, string workflowName)
    {
        InitializeComponent();
        _store = store;
        _connection = connection;
        _workflowName = workflowName;

        FileLog.Write($"[WorkflowRunsDialog] Created: connection={connection}, workflow={workflowName}");

        Title = $"Runs - {workflowName}";
        RunsHeader.Text = $"RUNS: {workflowName}";

        Loaded += async (_, _) =>
        {
            try
            {
                var runs = await Task.Run(() => _store.ListRuns(connection, workflowName));
                LoadRuns(runs);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowRunsDialog] Load FAILED: {ex.Message}");
                MessageBox.Show($"Failed to load runs:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private void LoadRuns(List<WorkflowRun> runs)
    {
        FileLog.Write($"[WorkflowRunsDialog] LoadRuns: {runs.Count} runs");
        _runEntries.Clear();

        foreach (var run in runs)
        {
            var statusColor = run.Status switch
            {
                "completed" => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                "failed" => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                "cancelled" => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
                _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            };

            _runEntries.Add(new RunEntry
            {
                Run = run,
                DisplayTime = FormatTime(run.StartedAt),
                StatusText = run.Status.ToUpperInvariant(),
                StatusColor = statusColor,
                StepSummary = $"{run.Steps.Count} steps",
            });
        }

        RunsList.ItemsSource = _runEntries;
    }

    private void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            RunsList_SelectionChangedCore(e);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRunsDialog] SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void RunsList_SelectionChangedCore(SelectionChangedEventArgs e)
    {
        if (RunsList.SelectedItem is not RunEntry entry)
        {
            StepsList.ItemsSource = null;
            ParamValuesText.Text = "";
            return;
        }

        FileLog.Write($"[WorkflowRunsDialog] Selected run: {entry.Run.Id}");

        // Show parameter values
        if (entry.Run.ParameterValues.Count > 0)
        {
            ParamValuesText.Text = "Parameters: " + string.Join(", ",
                entry.Run.ParameterValues.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
        }
        else
        {
            ParamValuesText.Text = "";
        }

        // Build step entries
        var ssDir = _store.RunScreenshotDir(_connection, _workflowName, entry.Run.Id);
        var stepEntries = new List<StepEntry>();

        foreach (var step in entry.Run.Steps)
        {
            var stepStatusColor = step.Status switch
            {
                "completed" => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                "failed" => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            };

            string? thumbPath = null;
            if (!string.IsNullOrEmpty(step.ScreenshotFile))
            {
                var fullPath = Path.Combine(ssDir, step.ScreenshotFile);
                if (File.Exists(fullPath))
                    thumbPath = fullPath;
            }

            var paramsDisplay = "";
            if (step.Params is { Count: > 0 })
            {
                paramsDisplay = string.Join(", ",
                    step.Params.Select(kv => $"{kv.Key}: {kv.Value}"));
            }

            stepEntries.Add(new StepEntry
            {
                Step = step,
                CommandDisplay = $"#{step.Index + 1} {step.Command}",
                ParamsDisplay = paramsDisplay,
                StatusText = step.Status.ToUpperInvariant(),
                StatusColor = stepStatusColor,
                DurationDisplay = $"{step.DurationMs}ms",
                ThumbnailPath = thumbPath,
                FullScreenshotPath = thumbPath,
            });
        }

        StepsList.ItemsSource = stepEntries;
    }

    private void StepsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            StepsList_MouseDoubleClickCore();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRunsDialog] MouseDoubleClick FAILED: {ex.Message}");
        }
    }

    private void StepsList_MouseDoubleClickCore()
    {
        if (StepsList.SelectedItem is not StepEntry entry) return;
        if (string.IsNullOrEmpty(entry.FullScreenshotPath)) return;
        if (!File.Exists(entry.FullScreenshotPath)) return;

        FileLog.Write($"[WorkflowRunsDialog] Opening screenshot: {entry.FullScreenshotPath}");

        var viewer = new Window
        {
            Title = $"Step #{entry.Step.Index + 1} - {entry.Step.Command}",
            Width = 900,
            Height = 600,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };

        var image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
        };

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(entry.FullScreenshotPath);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        image.Source = bitmap;

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = image,
        };

        viewer.Content = scroll;
        viewer.Show();
    }

    private static string FormatTime(string isoTime)
    {
        if (DateTime.TryParse(isoTime, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return isoTime;
    }

    private class RunEntry
    {
        public required WorkflowRun Run { get; set; }
        public string DisplayTime { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SolidColorBrush StatusColor { get; set; } = new(Colors.Gray);
        public string StepSummary { get; set; } = "";
    }

    private class StepEntry
    {
        public required WorkflowRunStep Step { get; set; }
        public string CommandDisplay { get; set; } = "";
        public string ParamsDisplay { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SolidColorBrush StatusColor { get; set; } = new(Colors.Gray);
        public string DurationDisplay { get; set; } = "";
        public string? ThumbnailPath { get; set; }
        public string? FullScreenshotPath { get; set; }
    }
}
