using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class UsageDashboardView : UserControl
{
    private static readonly ISolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly ISolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly ISolidColorBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly ISolidColorBrush CardBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly ISolidColorBrush BarBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly ISolidColorBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly ISolidColorBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly ISolidColorBrush GridlineBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly ISolidColorBrush StaleBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly ISolidColorBrush SeparatorBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

    private UsageHistoryStore? _historyStore;
    private ClaudeUsageService? _usageService;

    public UsageDashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Inject the usage service to subscribe to live updates.
    /// </summary>
    public void SetUsageService(ClaudeUsageService usageService)
    {
        FileLog.Write("[UsageDashboardView] SetUsageService");
        _usageService = usageService;
        _usageService.UsageUpdated += OnUsageUpdated;
        _ = LoadAsync();
    }

    /// <summary>
    /// Clean up event subscription.
    /// </summary>
    public void Detach()
    {
        FileLog.Write("[UsageDashboardView] Detach");
        if (_usageService != null)
        {
            _usageService.UsageUpdated -= OnUsageUpdated;
            _usageService = null;
        }
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[UsageDashboardView] BtnRefresh_Click");
        await LoadAsync();
    }

    /// <summary>
    /// Full load: fetch current usage from service and load history from store.
    /// </summary>
    public async Task LoadAsync()
    {
        FileLog.Write("[UsageDashboardView] LoadAsync");
        LoadingText.IsVisible = true;
        ContentScroller.IsVisible = false;

        var history = await Task.Run(() =>
        {
            _historyStore ??= new UsageHistoryStore();
            return _historyStore.LoadAll(TimeSpan.FromHours(24));
        });

        // Trigger a poll if service is available
        if (_usageService != null)
        {
            try
            {
                await _usageService.PollAllAccountsAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[UsageDashboardView] LoadAsync poll FAILED: {ex.Message}");
            }
        }

        RebuildTrendChart(history);

        LoadingText.IsVisible = false;
        ContentScroller.IsVisible = true;
        FileLog.Write("[UsageDashboardView] LoadAsync complete");
    }

    private void OnUsageUpdated(List<ClaudeUsageInfo> infos)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                FileLog.Write($"[UsageDashboardView] OnUsageUpdated: {infos.Count} accounts");

                RebuildAccountCards(infos);

                // Append to history store on background thread
                Task.Run(() => AppendToHistory(infos));

                // Rebuild trend chart
                Task.Run(() =>
                {
                    _historyStore ??= new UsageHistoryStore();
                    var history = _historyStore.LoadAll(TimeSpan.FromHours(24));
                    Dispatcher.UIThread.Post(() => RebuildTrendChart(history));
                });

                LoadingText.IsVisible = false;
                ContentScroller.IsVisible = true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[UsageDashboardView] OnUsageUpdated FAILED: {ex.Message}");
            }
        });
    }

    private void AppendToHistory(List<ClaudeUsageInfo> infos)
    {
        FileLog.Write($"[UsageDashboardView] AppendToHistory: {infos.Count} entries");
        _historyStore ??= new UsageHistoryStore();

        foreach (var info in infos)
        {
            if (info.HasData)
                _historyStore.Append(info);
        }
    }

    // ==================== ACCOUNT CARDS ====================

    private void RebuildAccountCards(List<ClaudeUsageInfo> infos)
    {
        FileLog.Write($"[UsageDashboardView] RebuildAccountCards: {infos.Count} accounts");

        // Preserve trend chart
        Control? trendChart = null;
        foreach (var child in ContentPanel.Children)
        {
            if (child is Control c && c.Tag as string == "TrendChart")
            {
                trendChart = c;
                break;
            }
        }

        ContentPanel.Children.Clear();

        foreach (var info in infos)
        {
            var card = BuildAccountCard(info);
            ContentPanel.Children.Add(card);
        }

        if (trendChart != null)
            ContentPanel.Children.Add(trendChart);
    }

    private Control BuildAccountCard(ClaudeUsageInfo info)
    {
        FileLog.Write($"[UsageDashboardView] BuildAccountCard: {info.AccountLabel}");

        var card = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var stack = new StackPanel();

        // Account header row
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var accountLabel = new TextBlock
        {
            Text = info.AccountLabel,
            Foreground = TextBrush,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
        };
        DockPanel.SetDock(accountLabel, Dock.Left);
        headerRow.Children.Add(accountLabel);

        var subType = new TextBlock
        {
            Text = info.SubscriptionType,
            Foreground = DimBrush,
            FontSize = 11,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
        };
        headerRow.Children.Add(subType);

        stack.Children.Add(headerRow);

        // Stale indicator
        if (info.IsStale)
        {
            var staleText = new TextBlock
            {
                Text = $"[STALE: {info.StaleReason}]",
                Foreground = StaleBrush,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
            };
            stack.Children.Add(staleText);
        }

        if (!info.HasData)
        {
            var noData = new TextBlock
            {
                Text = "No usage data available",
                Foreground = DimBrush,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
            };
            stack.Children.Add(noData);
            card.Child = stack;
            return card;
        }

        // Utilization bars
        stack.Children.Add(BuildUtilizationBar("5h", info.FiveHourUtilization, info.FiveHourResetsAt));
        stack.Children.Add(BuildUtilizationBar("7d", info.SevenDayUtilization, info.SevenDayResetsAt));

        if (info.OpusUtilization.HasValue)
            stack.Children.Add(BuildUtilizationBar("Opus", info.OpusUtilization.Value, info.OpusResetsAt));

        // Extra usage
        if (info.ExtraUsageSpent.HasValue || info.ExtraUsageLimit.HasValue)
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = SeparatorBrush,
                Margin = new Thickness(0, 6, 0, 6),
            });

            var spent = info.ExtraUsageSpent ?? 0;
            var limit = info.ExtraUsageLimit ?? 0;
            var extraText = new TextBlock
            {
                Text = $"Extra usage: ${spent:F2} / ${limit:F2}",
                Foreground = DimBrush,
                FontSize = 12,
            };
            stack.Children.Add(extraText);
        }

        card.Child = stack;
        return card;
    }

    private static Control BuildUtilizationBar(string label, double utilization, DateTimeOffset? resetsAt)
    {
        var pct = Math.Clamp(utilization, 0, 100);
        var fraction = pct / 100.0;

        var container = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };

        // Label row
        var labelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };

        var labelText = new TextBlock
        {
            Text = $"{label}: {pct:F0}%",
            Foreground = TextBrush,
            FontSize = 12,
        };
        DockPanel.SetDock(labelText, Dock.Left);
        labelRow.Children.Add(labelText);

        if (resetsAt.HasValue)
        {
            var resetText = new TextBlock
            {
                Text = FormatResetCountdown(resetsAt.Value),
                Foreground = DimBrush,
                FontSize = 11,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            };
            labelRow.Children.Add(resetText);
        }

        container.Children.Add(labelRow);

        // Progress bar
        var barHeight = 8.0;
        var barGrid = new Grid
        {
            Height = barHeight,
            ClipToBounds = true,
        };

        var bgBorder = new Border
        {
            Background = BarBackgroundBrush,
            CornerRadius = new CornerRadius(4),
        };
        barGrid.Children.Add(bgBorder);

        if (fraction > 0)
        {
            var fgBorder = new Border
            {
                Background = GetUtilizationBrush(pct),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            };

            // In Avalonia, use PropertyChanged on Bounds to size the fill bar
            barGrid.PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty)
                {
                    fgBorder.Width = barGrid.Bounds.Width * fraction;
                }
            };

            barGrid.Children.Add(fgBorder);
        }

        container.Children.Add(barGrid);
        return container;
    }

    private static ISolidColorBrush GetUtilizationBrush(double pct)
    {
        if (pct >= 85) return RedBrush;
        if (pct >= 60) return YellowBrush;
        return GreenBrush;
    }

    private static string FormatResetCountdown(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0)
            return "resetting now";

        if (remaining.TotalHours >= 1)
        {
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return $"resets in {hours}h {minutes}m";
        }

        return $"resets in {remaining.Minutes}m";
    }

    // ==================== TREND CHART ====================

    private void RebuildTrendChart(List<UsageHistoryEntry> history)
    {
        FileLog.Write($"[UsageDashboardView] RebuildTrendChart: {history.Count} data points");

        // Remove old trend chart
        Control? oldChart = null;
        foreach (var child in ContentPanel.Children)
        {
            if (child is Control c && c.Tag as string == "TrendChart")
            {
                oldChart = c;
                break;
            }
        }
        if (oldChart != null)
            ContentPanel.Children.Remove(oldChart);

        if (history.Count < 2)
        {
            FileLog.Write("[UsageDashboardView] RebuildTrendChart: not enough data for chart");
            return;
        }

        var chartContainer = new StackPanel
        {
            Tag = "TrendChart",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var header = new TextBlock
        {
            Text = "5-HOUR UTILIZATION (24H)",
            Foreground = AccentBrush,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        };
        chartContainer.Children.Add(header);

        var chartCard = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
        };

        const double canvasWidth = 300;
        const double canvasHeight = 120;

        var canvas = new Canvas
        {
            Width = canvasWidth,
            Height = canvasHeight,
            ClipToBounds = true,
        };

        // Y-axis gridlines at 25%, 50%, 75%
        foreach (var gridPct in new[] { 25, 50, 75 })
        {
            var y = canvasHeight - (canvasHeight * gridPct / 100.0);

            var gridLine = new Line
            {
                StartPoint = new Point(0, y),
                EndPoint = new Point(canvasWidth, y),
                Stroke = GridlineBrush,
                StrokeThickness = 1,
                StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            };
            canvas.Children.Add(gridLine);

            var gridLabel = new TextBlock
            {
                Text = $"{gridPct}%",
                Foreground = DimBrush,
                FontSize = 9,
            };
            Canvas.SetLeft(gridLabel, 2);
            Canvas.SetTop(gridLabel, y - 12);
            canvas.Children.Add(gridLabel);
        }

        // Sort history by timestamp
        var sorted = history.OrderBy(h => h.Timestamp).ToList();
        var startTime = sorted[0].Timestamp;
        var endTime = sorted[^1].Timestamp;
        var timeRange = (endTime - startTime).TotalSeconds;

        if (timeRange <= 0)
            timeRange = 1;

        // Build polyline points
        var points = new List<Point>();
        foreach (var entry in sorted)
        {
            var xFraction = (entry.Timestamp - startTime).TotalSeconds / timeRange;
            var x = xFraction * canvasWidth;
            var y = canvasHeight - (Math.Clamp(entry.FiveHourUtilization, 0, 100) / 100.0 * canvasHeight);
            points.Add(new Point(x, y));
        }

        var polyline = new Polyline
        {
            Points = new global::Avalonia.Collections.AvaloniaList<Point>(points),
            Stroke = AccentBrush,
            StrokeThickness = 1.5,
            StrokeJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(polyline);

        // X-axis time labels
        var timeLabels = new[] { startTime, startTime + (endTime - startTime) / 2, endTime };
        var xPositions = new[] { 0.0, canvasWidth / 2 - 20, canvasWidth - 40 };

        for (int i = 0; i < timeLabels.Length; i++)
        {
            var timeLabel = new TextBlock
            {
                Text = timeLabels[i].ToLocalTime().ToString("HH:mm"),
                Foreground = DimBrush,
                FontSize = 9,
            };
            Canvas.SetLeft(timeLabel, xPositions[i]);
            Canvas.SetTop(timeLabel, canvasHeight + 2);
            canvas.Children.Add(timeLabel);
        }

        chartCard.Child = canvas;
        chartContainer.Children.Add(chartCard);
        ContentPanel.Children.Add(chartContainer);

        FileLog.Write("[UsageDashboardView] RebuildTrendChart complete");
    }
}
