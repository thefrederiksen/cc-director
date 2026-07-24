using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The pinned "Browsers" group in the left rail: one row per drivable automation browser, each with a
/// status dot and the single next action (Start / Sign in / Attach). Rows render the Core fold
/// (<see cref="AutomationBrowserViewFold"/>) verbatim - this control decides layout, never meaning.
/// When Browser Harness is not installed the group stays visible but dimmed, with an inline install
/// link, so the feature advertises itself instead of hiding (mockup state 2).
/// </summary>
public partial class BrowsersRailGroup : UserControl
{
    /// <summary>Raised when the user wants the management surface (Settings > Browsers): the header
    /// +, a click on a row, or the "Add a browser..." empty-state link.</summary>
    public event EventHandler? ManageRequested;

    /// <summary>Raised with a short user-facing message (action feedback or a failure) for the host
    /// window's notification strip.</summary>
    public event EventHandler<string>? Notified;

    private static readonly IBrush AccentBrush = Brush.Parse("#4A9EFF");
    private static readonly IBrush AmberBrush = Brush.Parse("#F0B848");

    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<AutomationBrowserView> _views = Array.Empty<AutomationBrowserView>();
    private bool _harnessInstalled;
    private bool _expanded = true;
    private bool _refreshing;

    public BrowsersRailGroup()
    {
        InitializeComponent();

        // Status is a live port probe, so it can drift (the user closes a browser window by hand);
        // a slow background re-poll keeps the dots honest without hammering the ports.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();

        AttachedToVisualTree += (_, _) =>
        {
            _refreshTimer.Start();
            _ = RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// Re-read the registry, probe each browser's live status, and repaint the group. Safe to call
    /// from anywhere; overlapping calls collapse into one.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var harnessInstalled = false;
            IReadOnlyList<AutomationBrowserView> views = Array.Empty<AutomationBrowserView>();
            await Task.Run(async () =>
            {
                harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled();
                views = await AutomationBrowserViewFold.ListAsync().ConfigureAwait(false);
            });

            _views = views;
            _harnessInstalled = harnessInstalled;
            Render(views, harnessInstalled);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowsersRailGroup] RefreshAsync FAILED: {ex.Message}");
            Notified?.Invoke(this, $"Could not read the browsers list: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Render(IReadOnlyList<AutomationBrowserView> views, bool harnessInstalled)
    {
        CountBadge.IsVisible = harnessInstalled && views.Count > 0;
        CountText.Text = views.Count.ToString();
        SetupLabel.IsVisible = !harnessInstalled;
        InstallLink.IsVisible = _expanded && !harnessInstalled;
        AddFirstLink.IsVisible = _expanded && harnessInstalled && views.Count == 0;
        BodyPanel.IsVisible = _expanded;
        Chevron.Data = Geometry.Parse(_expanded ? "M 0,0 L 4,4 L 8,0" : "M 0,0 L 4,4 L 0,8");

        RowsList.ItemsSource = views.Select(v => new BrowserRailRow
        {
            Id = v.Id,
            Name = v.Name,
            Subtitle = v.Subtitle,
            DotBrush = StatusPalette.BrushFor(v.DotColor),
            // Dimmed advertising rows when the harness is missing: discoverable, not operable.
            RowOpacity = harnessInstalled ? 1.0 : 0.45,
            ActionVisible = harnessInstalled,
            ActionLabel = v.ActionLabel,
            ActionForeground = v.Status == AutomationBrowserStatus.NeedsSignIn ? AmberBrush : AccentBrush,
            ActionToolTip = v.Status switch
            {
                AutomationBrowserStatus.Stopped => "Start this browser",
                AutomationBrowserStatus.NeedsSignIn => "Open the sign-in page for the one-time hand sign-in",
                _ => "Copy the command that points an agent's Browser Harness at this browser",
            },
            RowToolTip = $"{v.Subtitle} ({v.StatusLabel}). Click to manage in Settings.",
        }).ToList();
    }

    // ---- header ----

    private void HeaderButton_Click(object? sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        FileLog.Write($"[BrowsersRailGroup] HeaderButton_Click: expanded={_expanded}");
        Render(_views, _harnessInstalled);
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowsersRailGroup] AddButton_Click");
        ManageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InstallLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write($"[BrowsersRailGroup] InstallLink_Click -> {AutomationBrowserViewFold.HarnessInstallUrl}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AutomationBrowserViewFold.HarnessInstallUrl)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowsersRailGroup] InstallLink_Click FAILED: {ex.Message}");
            Notified?.Invoke(this, $"Could not open the install guide: {ex.Message}");
        }
    }

    // ---- rows ----

    private void Row_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowsersRailGroup] Row_Click -> manage");
        ManageRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void RowAction_Click(object? sender, RoutedEventArgs e)
    {
        // Stop the click from also triggering the row's own click (which navigates to Settings).
        e.Handled = true;

        var id = (sender as Button)?.Tag as string;
        var view = _views.FirstOrDefault(v => v.Id == id);
        if (view is null) return;

        try
        {
            FileLog.Write($"[BrowsersRailGroup] RowAction_Click: id={view.Id}, status={view.Status}");
            switch (view.Status)
            {
                case AutomationBrowserStatus.Stopped:
                    Notified?.Invoke(this, $"Starting \"{view.Name}\"...");
                    await Task.Run(() => AutomationBrowserService.LaunchAsync(view.Id));
                    Notified?.Invoke(this, $"\"{view.Name}\" is up.");
                    break;

                case AutomationBrowserStatus.NeedsSignIn:
                    await RunSignInFlowAsync(view);
                    break;

                case AutomationBrowserStatus.Ready:
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.Clipboard is null)
                        throw new InvalidOperationException("The clipboard is not available.");
                    await top.Clipboard.SetTextAsync(view.AttachCommand);
                    Notified?.Invoke(this, $"Attach command for \"{view.Name}\" copied to the clipboard.");
                    break;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowsersRailGroup] RowAction_Click FAILED: id={id}, {ex.Message}");
            Notified?.Invoke(this, ex.Message);
        }
    }

    /// <summary>The one-time human sign-in, via the shared <see cref="BrowserSignInFlow"/> so the rail
    /// and the Settings tab run the identical flow and wording.</summary>
    private async Task RunSignInFlowAsync(AutomationBrowserView view)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            throw new InvalidOperationException("No owner window for the sign-in confirmation dialog.");

        Notified?.Invoke(this, $"Opening the sign-in page in \"{view.Name}\"...");
        if (await BrowserSignInFlow.RunAsync(owner, view))
            Notified?.Invoke(this, $"\"{view.Name}\" is signed in and ready to drive.");
    }

    /// <summary>One rendered rail row: the fold's strings plus the brushes this surface maps them to.</summary>
    public sealed class BrowserRailRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public IBrush DotBrush { get; init; } = Brushes.Gray;
        public double RowOpacity { get; init; } = 1.0;
        public bool ActionVisible { get; init; }
        public string ActionLabel { get; init; } = "";
        public IBrush ActionForeground { get; init; } = Brushes.White;
        public string ActionToolTip { get; init; } = "";
        public string RowToolTip { get; init; } = "";
    }
}
