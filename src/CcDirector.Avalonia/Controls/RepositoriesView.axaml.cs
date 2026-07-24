using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>One recommendation card - display data only; the engine folded the strings.</summary>
public sealed class RecommendationRowItem
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Why { get; init; } = "";
    public string? RepoPath { get; init; }
    public bool HasRepo => RepoPath is { Length: > 0 };
    public IBrush StripeBrush { get; init; } = Brushes.Gray;

    public static RecommendationRowItem From(Recommendation r) => new()
    {
        Title = r.Title,
        Body = r.Body,
        Why = r.Why,
        RepoPath = r.RepoPath,
        StripeBrush = r.Severity switch
        {
            1 => new SolidColorBrush(Color.Parse("#22C55E")),
            2 => new SolidColorBrush(Color.Parse("#F59E0B")),
            _ => new SolidColorBrush(Color.Parse("#3B82F6")),
        },
    };
}

/// <summary>
/// The Repositories home: a left sub-rail hosting the live local-repository list and the root-folder
/// registration, shown as a pinned, non-modal view inside the main window (issue #507, phase 4).
/// It renders the always-current model from the background <see cref="RepositoryMonitor"/>; it does
/// not scan. Root folders can be added, edited, and removed here - changing them triggers a rescan.
/// </summary>
public partial class RepositoriesView : UserControl
{
    private static readonly IBrush RowBg = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush RowBorder = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#4A9EFF"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private bool _attached;
    private RepositoryMonitor? _monitor;
    private RootDirectoryStore? _store;
    private Action? _onRefresh;

    /// <summary>The owner chose a hand-off on a repo; the host spawns a session with the brief staged.</summary>
    public event Action<string, string>? HandToAgentRequested;

    /// <summary>Live sessions provider, forwarded to the detail's worktrees panel.</summary>
    public Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider
    {
        get => DetailPage.LiveSessionsProvider;
        set => DetailPage.LiveSessionsProvider = value;
    }

    public RepositoriesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wire the live model and the registered roots. Idempotent - safe to call each time the view is
    /// opened; the monitor subscription is set up once, the roots list is refreshed each time.
    /// </summary>
    public void Attach(RepositoryMonitor monitor, RootDirectoryStore store, Action onRefreshRequested)
    {
        _monitor = monitor;
        _store = store;
        _onRefresh = onRefreshRequested;
        if (!_attached)
        {
            FileLog.Write("[RepositoriesView] Attach");
            ReposPage.RefreshRequested += onRefreshRequested;
            ReposPage.RepoOpenRequested += OpenDetail;
            DetailPage.BackRequested += CloseDetail;
            DetailPage.HandToAgentRequested += (path, brief) => HandToAgentRequested?.Invoke(path, brief);
            ReposPage.Attach(monitor);
            _attached = true;
        }
        RenderRoots();
    }

    /// <summary>Drill into one repository's detail screen.</summary>
    private void OpenDetail(string repoPath)
    {
        if (_monitor is null)
            return;
        ReposPage.IsVisible = false;
        RootsPage.IsVisible = false;
        DetailPage.IsVisible = true;
        DetailPage.Attach(_monitor, repoPath);
    }

    private void CloseDetail()
    {
        DetailPage.Detach();
        DetailPage.IsVisible = false;
        ShowRoots(false);
    }

    private void ReposRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("repos");
    private void RootsRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("roots");
    private void RecoRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("reco");

    private void ShowRoots(bool roots) => ShowPage(roots ? "roots" : "repos");

    private void ShowPage(string page)
    {
        DetailPage.IsVisible = false;
        ReposPage.IsVisible = page == "repos";
        RootsPage.IsVisible = page == "roots";
        RecoPage.IsVisible = page == "reco";
        SetActive(ReposRailButton, page == "repos");
        SetActive(RootsRailButton, page == "roots");
        SetActive(RecoRailButton, page == "reco");
        if (page == "reco")
            RenderRecommendations();
    }

    // ----- recommendations (folded by the engine; this only renders) -----

    /// <summary>Recompute + render the recommendation cards and the rail badge.</summary>
    public void RenderRecommendations()
    {
        if (_monitor is null)
            return;
        var recs = RecommendationEngine.Evaluate(_monitor.Snapshot());
        RecoList.ItemsSource = recs.Select(RecommendationRowItem.From).ToList();
        RecoEmptyText.IsVisible = recs.Count == 0;
        RecoBadgeText.Text = recs.Count.ToString();
        RecoBadge.IsVisible = recs.Count > 0;
    }

    private void RecoShow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not RecommendationRowItem row)
            return;
        if (row.RepoPath is { Length: > 0 } path)
            OpenDetail(path);
        else
            ShowPage("repos"); // the fleet-wide reap summary: the list shows where the safe worktrees are
    }

    private async void RecoHandOff_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_monitor is null || (sender as Button)?.DataContext is not RecommendationRowItem row || row.RepoPath is null)
                return;
            var repo = _monitor.FindForPath(row.RepoPath);
            var window = TopLevel.GetTopLevel(this) as Window;
            if (repo is null || window is null)
                return;
            var dialog = new global::CcDirector.Avalonia.HandToAgentDialog(repo);
            var brief = await dialog.ShowDialog<string?>(window);
            if (!string.IsNullOrWhiteSpace(brief))
                HandToAgentRequested?.Invoke(row.RepoPath, brief);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoriesView] RecoHandOff_Click FAILED: {ex.Message}");
        }
    }

    private static void SetActive(Button button, bool active)
    {
        if (active) button.Classes.Add("active");
        else button.Classes.Remove("active");
    }

    // ----- root-folder registration -----

    private async void AddRootButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null || _store is null)
            return;
        var dialog = new global::CcDirector.Avalonia.RootDirectoryDialog();
        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.Result is { } config)
        {
            FileLog.Write($"[RepositoriesView] add root: {config.Path}");
            _store.Add(config);
            RenderRoots();
            _onRefresh?.Invoke();
        }
    }

    private async void EditRoot(int index)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null || _store is null || index < 0 || index >= _store.Roots.Count)
            return;
        var dialog = new global::CcDirector.Avalonia.RootDirectoryDialog(_store.Roots[index]);
        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.Result is { } config)
        {
            FileLog.Write($"[RepositoriesView] edit root #{index}: {config.Path}");
            _store.Update(index, config);
            RenderRoots();
            _onRefresh?.Invoke();
        }
    }

    private void RemoveRoot(int index)
    {
        if (_store is null || index < 0 || index >= _store.Roots.Count)
            return;
        FileLog.Write($"[RepositoriesView] remove root #{index}: {_store.Roots[index].Path}");
        _store.Remove(index);
        RenderRoots();
        _onRefresh?.Invoke();
    }

    private void RenderRoots()
    {
        RootsContainer.Children.Clear();
        if (_store is null)
            return;

        for (int i = 0; i < _store.Roots.Count; i++)
        {
            int index = i; // capture for the button handlers
            var r = _store.Roots[i];

            var label = new TextBlock { Text = string.IsNullOrWhiteSpace(r.Label) ? r.Path : r.Label, Foreground = Text, FontSize = 13, FontWeight = FontWeight.Medium };
            var provider = new TextBlock { Text = r.ProviderDisplayName, Foreground = Blue, FontSize = 10.5, FontFamily = Mono, Margin = new global::Avalonia.Thickness(0, 1, 0, 0) };
            var path = new TextBlock { Text = r.Path, Foreground = Muted, FontSize = 10.5, FontFamily = Mono, Margin = new global::Avalonia.Thickness(0, 1, 0, 0) };
            var info = new StackPanel { Spacing = 1, Children = { label, provider, path }, VerticalAlignment = VerticalAlignment.Center };

            var edit = new Button { Content = "Edit", Background = RowBorder, Foreground = Text, Padding = new global::Avalonia.Thickness(10, 4), FontSize = 11, Margin = new global::Avalonia.Thickness(0, 0, 6, 0) };
            edit.Click += (_, _) => EditRoot(index);
            var remove = new Button { Content = "Remove", Background = new SolidColorBrush(Color.Parse("#5A2A2A")), Foreground = new SolidColorBrush(Color.Parse("#F0A6A6")), Padding = new global::Avalonia.Thickness(10, 4), FontSize = 11 };
            remove.Click += (_, _) => RemoveRoot(index);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(edit);
            actions.Children.Add(remove);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            RootsContainer.Children.Add(new Border
            {
                Background = RowBg,
                BorderBrush = RowBorder,
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(12, 8),
                Child = grid,
            });
        }
    }
}
