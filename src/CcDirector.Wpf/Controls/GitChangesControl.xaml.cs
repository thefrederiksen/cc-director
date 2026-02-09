using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Git;

namespace CcDirector.Wpf.Controls;

public partial class GitChangesControl : UserControl
{
    private static readonly SolidColorBrush BrushModified = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)));  // amber
    private static readonly SolidColorBrush BrushAdded = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));     // green
    private static readonly SolidColorBrush BrushDeleted = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));   // red
    private static readonly SolidColorBrush BrushRenamed = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));   // blue
    private static readonly SolidColorBrush BrushUntracked = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))); // gray
    private static readonly SolidColorBrush BrushDefault = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));

    private readonly GitStatusProvider _provider = new();
    private DispatcherTimer? _pollTimer;
    private string? _repoPath;

    public GitChangesControl()
    {
        InitializeComponent();
    }

    public void Attach(string repoPath)
    {
        Detach();
        _repoPath = repoPath;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        // Immediate first poll
        _ = RefreshAsync();
    }

    public void Detach()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _repoPath = null;

        StagedList.ItemsSource = null;
        UnstagedList.ItemsSource = null;
        StagedSection.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_repoPath == null) return;

        var result = await _provider.GetStatusAsync(_repoPath);
        if (!result.Success) return;

        var stagedVms = result.StagedChanges.Select(f => new GitFileViewModel(f)).ToList();
        var unstagedVms = result.UnstagedChanges.Select(f => new GitFileViewModel(f)).ToList();

        StagedList.ItemsSource = stagedVms;
        UnstagedList.ItemsSource = unstagedVms;

        if (stagedVms.Count > 0)
        {
            StagedSection.Visibility = Visibility.Visible;
            StagedCount.Text = $"({stagedVms.Count})";
        }
        else
        {
            StagedSection.Visibility = Visibility.Collapsed;
        }

        UnstagedCount.Text = $"({unstagedVms.Count})";
        EmptyText.Visibility = stagedVms.Count == 0 && unstagedVms.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetStatusBrush(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified => BrushModified,
        GitFileStatus.Added => BrushAdded,
        GitFileStatus.Deleted => BrushDeleted,
        GitFileStatus.Renamed => BrushRenamed,
        GitFileStatus.Copied => BrushRenamed,
        GitFileStatus.Untracked => BrushUntracked,
        _ => BrushDefault
    };

    public class GitFileViewModel
    {
        public string FileName { get; }
        public string FilePath { get; }
        public string StatusChar { get; }
        public SolidColorBrush StatusBrush { get; }

        public GitFileViewModel(GitFileEntry entry)
        {
            FileName = entry.FileName;
            FilePath = entry.FilePath;
            StatusChar = entry.StatusChar;
            StatusBrush = GetStatusBrush(entry.Status);
        }
    }
}
