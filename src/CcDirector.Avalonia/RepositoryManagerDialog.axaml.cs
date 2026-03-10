using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class RepositoryManagerDialog : Window
{
    private readonly RootDirectoryStore _rootStore;
    private readonly GitStatusProvider _gitStatusProvider = new();
    private List<MergedRepoItem> _allRepos = [];
    private RootDirectoryConfig? _selectedRoot;

    /// <summary>If the user clicks "Launch Session", this is set to the local path.</summary>
    public string? LaunchSessionPath { get; private set; }

    public RepositoryManagerDialog(RootDirectoryStore rootStore)
    {
        FileLog.Write("[RepositoryManagerDialog] Constructor: entered");
        InitializeComponent();
        _rootStore = rootStore;
        RefreshRootList();

        Loaded += async (_, _) =>
        {
            if (_rootStore.Roots.Count > 0)
                await RefreshAllReposAsync();
        };
    }

    // Parameterless constructor for XAML designer
    public RepositoryManagerDialog() : this(new RootDirectoryStore()) { }

    // -- Root Directory Management ------------------------------------------------

    private void RefreshRootList()
    {
        RootList.ItemsSource = null;
        RootList.ItemsSource = _rootStore.Roots;
    }

    private async void BtnAddRoot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnAddRoot_Click: entered");
        try
        {
            var dialog = new RootDirectoryDialog();
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                _rootStore.Add(dialog.Result);
                RefreshRootList();
                RootList.SelectedIndex = _rootStore.Roots.Count - 1;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnAddRoot_Click FAILED: {ex.Message}");
            StatusBarText.Text = $"Failed to add root directory: {ex.Message}";
        }
    }

    private async void BtnEditRoot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnEditRoot_Click: entered");
        try
        {
            if (sender is not Button { Tag: RootDirectoryConfig config }) return;
            var index = _rootStore.Roots.ToList().IndexOf(config);
            if (index < 0) return;

            var dialog = new RootDirectoryDialog(config);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                _rootStore.Update(index, dialog.Result);
                RefreshRootList();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnEditRoot_Click FAILED: {ex.Message}");
            StatusBarText.Text = $"Failed to edit root directory: {ex.Message}";
        }
    }

    private void BtnRemoveRoot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnRemoveRoot_Click: entered");
        try
        {
            if (sender is not Button { Tag: RootDirectoryConfig config }) return;
            var index = _rootStore.Roots.ToList().IndexOf(config);
            if (index < 0) return;

            // TODO: Replace with proper confirmation dialog when available in Avalonia port.
            // For now, proceed without confirmation since this only removes the registration.
            FileLog.Write($"[RepositoryManagerDialog] Removing root directory: {config.Label}");

            _rootStore.Remove(index);
            RefreshRootList();
            _allRepos.RemoveAll(r => r.RootLabel == config.Label);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnRemoveRoot_Click FAILED: {ex.Message}");
        }
    }

    private void BtnShowAll_Click(object? sender, RoutedEventArgs e)
    {
        RootList.SelectedIndex = -1;
        _selectedRoot = null;
        ApplyFilter();
    }

    private async void RootList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedRoot = RootList.SelectedItem as RootDirectoryConfig;
        if (_selectedRoot != null)
        {
            // If we have no repos loaded yet for this root, refresh
            if (!_allRepos.Any(r => r.RootLabel == _selectedRoot.Label))
                await RefreshReposForRootAsync(_selectedRoot);
        }
        ApplyFilter();
    }

    // -- Repo Refresh & Merge Logic -----------------------------------------------

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnRefresh_Click: entered");
        try
        {
            await RefreshAllReposAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnRefresh_Click FAILED: {ex.Message}");
            StatusBarText.Text = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RefreshAllReposAsync()
    {
        _allRepos.Clear();
        ShowLoading("Loading repositories...");

        foreach (var root in _rootStore.Roots)
        {
            await RefreshReposForRootAsync(root);
        }

        LastRefreshText.Text = $"Last refreshed: {DateTime.Now:HH:mm}";
        ApplyFilter();
    }

    private async Task RefreshReposForRootAsync(RootDirectoryConfig root)
    {
        FileLog.Write($"[RepositoryManagerDialog] RefreshReposForRootAsync: label={root.Label}, provider={root.Provider}");
        ShowLoading($"Scanning {root.Label}...");

        // Remove old entries for this root
        _allRepos.RemoveAll(r => r.RootLabel == root.Label);

        // 1. Scan local repos
        var localRepos = await Task.Run(() => RemoteRepoProvider.ScanLocalRepos(root.Path));

        // 2. Fetch remote repos (if applicable)
        List<RemoteRepoInfo> remoteRepos = [];
        string? remoteError = null;

        if (root.Provider == GitProvider.GitHub)
        {
            ShowLoading($"Querying GitHub for {root.Label}...");
            (remoteRepos, remoteError) = await RemoteRepoProvider.ListGitHubReposAsync();
        }
        else if (root.Provider == GitProvider.AzureDevOps &&
                 !string.IsNullOrWhiteSpace(root.AzureOrg) &&
                 !string.IsNullOrWhiteSpace(root.AzureProject))
        {
            ShowLoading($"Querying Azure DevOps for {root.Label}...");
            (remoteRepos, remoteError) = await RemoteRepoProvider.ListAzureDevOpsReposAsync(
                root.AzureOrg, root.AzureProject);
        }

        if (remoteError != null)
        {
            StatusBarText.Text = $"{root.Label}: {remoteError}";
        }

        // 3. Merge
        var merged = MergeRepos(root, localRepos, remoteRepos);

        // 4. Get git status for local repos (throttled)
        var semaphore = new SemaphoreSlim(4);
        var statusTasks = merged
            .Where(r => r.IsLocal)
            .Select(async repo =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var count = await _gitStatusProvider.GetCountAsync(repo.LocalPath!);
                    repo.UncommittedCount = count;
                    repo.RefreshStatus();
                }
                finally
                {
                    semaphore.Release();
                }
            });
        await Task.WhenAll(statusTasks);

        _allRepos.AddRange(merged);
        FileLog.Write($"[RepositoryManagerDialog] RefreshReposForRootAsync: {root.Label} yielded {merged.Count} repos");
    }

    private static List<MergedRepoItem> MergeRepos(
        RootDirectoryConfig root,
        List<(string Name, string Path)> localRepos,
        List<RemoteRepoInfo> remoteRepos)
    {
        var result = new List<MergedRepoItem>();
        var localByName = localRepos.ToDictionary(
            r => r.Name, r => r.Path, StringComparer.OrdinalIgnoreCase);

        // Process remote repos first (matched + remote-only)
        foreach (var remote in remoteRepos)
        {
            var isLocal = localByName.TryGetValue(remote.Name, out var localPath);
            result.Add(new MergedRepoItem
            {
                Name = remote.Name,
                LocalPath = isLocal ? localPath : null,
                RemoteUrl = remote.Url,
                RootLabel = root.Label,
                RootPath = root.Path,
                IsLocal = isLocal,
                IsRemote = true,
                Description = remote.Description,
                IsPrivate = remote.IsPrivate
            });
            if (isLocal)
                localByName.Remove(remote.Name);
        }

        // Remaining local-only repos
        foreach (var (name, path) in localByName)
        {
            result.Add(new MergedRepoItem
            {
                Name = name,
                LocalPath = path,
                RootLabel = root.Label,
                RootPath = root.Path,
                IsLocal = true,
                IsRemote = false
            });
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // -- Filtering & Display ------------------------------------------------------

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text?.Trim() ?? string.Empty;
        var filtered = _allRepos.AsEnumerable();

        // Filter by selected root
        if (_selectedRoot != null)
            filtered = filtered.Where(r => r.RootLabel == _selectedRoot.Label);

        // Filter by text
        if (!string.IsNullOrEmpty(filter))
            filtered = filtered.Where(r =>
                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (r.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.RootLabel.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = filtered.ToList();
        RepoList.ItemsSource = list;

        if (list.Count > 0)
        {
            RepoList.IsVisible = true;
            LoadingText.IsVisible = false;
        }
        else if (_allRepos.Count == 0)
        {
            ShowLoading(_rootStore.Roots.Count == 0
                ? "Add a root directory to get started."
                : "Click Refresh to load repositories.");
        }
        else
        {
            ShowLoading("No matching repositories.");
        }

        var localCount = list.Count(r => r.IsLocal);
        var remoteOnlyCount = list.Count(r => !r.IsLocal);
        StatusBarText.Text = $"{list.Count} repos ({localCount} local, {remoteOnlyCount} remote only)";

        UpdateActionButtons();
    }

    private void ShowLoading(string message)
    {
        LoadingText.Text = message;
        LoadingText.IsVisible = true;
        RepoList.IsVisible = false;
    }

    // -- Repo Selection & Actions -------------------------------------------------

    private void RepoList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
    }

    private void RepoList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RepoList.SelectedItem is MergedRepoItem { IsLocal: true } repo)
        {
            LaunchSessionPath = repo.LocalPath;
            Close(true);
        }
    }

    private void UpdateActionButtons()
    {
        var repo = RepoList.SelectedItem as MergedRepoItem;
        var hasSelection = repo != null;
        var isLocal = repo?.IsLocal == true;
        var isRemoteOnly = repo is { IsLocal: false, IsRemote: true };

        BtnLaunchSession.IsEnabled = isLocal;
        BtnOpenExplorer.IsEnabled = isLocal;
        BtnOpenVsCode.IsEnabled = isLocal;
        BtnCloneSelected.IsEnabled = isRemoteOnly;
        BtnDeleteLocal.IsEnabled = isLocal;

        SelectedRepoText.Text = hasSelection
            ? $"{repo!.Name}  ({repo.LocalPath ?? repo.RemoteUrl ?? ""})"
            : "";
    }

    private void BtnLaunchSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnLaunchSession_Click: entered");
        try
        {
            if (RepoList.SelectedItem is not MergedRepoItem { IsLocal: true } repo) return;
            LaunchSessionPath = repo.LocalPath;
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnLaunchSession_Click FAILED: {ex.Message}");
        }
    }

    private void BtnOpenExplorer_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnOpenExplorer_Click: entered");
        try
        {
            if (RepoList.SelectedItem is not MergedRepoItem { IsLocal: true, LocalPath: not null } repo) return;
            Process.Start(new ProcessStartInfo { FileName = repo.LocalPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnOpenExplorer_Click FAILED: {ex.Message}");
        }
    }

    private void BtnOpenVsCode_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnOpenVsCode_Click: entered");
        try
        {
            if (RepoList.SelectedItem is not MergedRepoItem { IsLocal: true, LocalPath: not null } repo) return;
            Process.Start(new ProcessStartInfo("code", $"\"{repo.LocalPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnOpenVsCode_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnCloneSelected_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnCloneSelected_Click: entered");
        try
        {
            if (RepoList.SelectedItem is not MergedRepoItem repo) return;
            if (string.IsNullOrWhiteSpace(repo.RemoteUrl) || string.IsNullOrWhiteSpace(repo.RootPath)) return;

            var destPath = Path.Combine(repo.RootPath, repo.Name);
            if (Directory.Exists(destPath))
            {
                // TODO: Replace with proper Avalonia message dialog when available.
                FileLog.Write($"[RepositoryManagerDialog] Clone aborted - directory already exists: {destPath}");
                StatusBarText.Text = $"Directory already exists: {destPath}";
                return;
            }

            StatusBarText.Text = $"Cloning {repo.Name}...";
            BtnCloneSelected.IsEnabled = false;

            var exitCode = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone \"{repo.RemoteUrl}\" \"{destPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) return -1;
                proc.WaitForExit();
                return proc.ExitCode;
            });

            if (exitCode == 0)
            {
                FileLog.Write($"[RepositoryManagerDialog] Clone succeeded: {destPath}");
                repo.LocalPath = destPath;
                repo.IsLocal = true;
                repo.RefreshStatus();
                StatusBarText.Text = $"Cloned {repo.Name} successfully.";

                // Also register in the main RepositoryRegistry
                var app = (App)Application.Current!;
                app.RepositoryRegistry.TryAdd(destPath);
            }
            else
            {
                StatusBarText.Text = $"Clone failed for {repo.Name} (exit code {exitCode}).";
                // TODO: Replace with proper Avalonia message dialog when available.
                FileLog.Write($"[RepositoryManagerDialog] git clone failed with exit code {exitCode}");
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnCloneSelected_Click FAILED: {ex.Message}");
            StatusBarText.Text = $"Clone failed: {ex.Message}";
        }
    }

    private async void BtnDeleteLocal_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RepositoryManagerDialog] BtnDeleteLocal_Click: entered");
        try
        {
            if (RepoList.SelectedItem is not MergedRepoItem { IsLocal: true, LocalPath: not null } repo) return;

            // TODO: Replace with proper Avalonia confirmation dialog when available.
            // This is a destructive operation -- a confirmation dialog is essential.
            FileLog.Write($"[RepositoryManagerDialog] WARNING: Deleting local repo without confirmation dialog: {repo.LocalPath}");

            StatusBarText.Text = $"Deleting {repo.Name}...";

            await Task.Run(() =>
            {
                if (Directory.Exists(repo.LocalPath))
                    Directory.Delete(repo.LocalPath, recursive: true);
            });

            FileLog.Write($"[RepositoryManagerDialog] Deleted local repo: {repo.LocalPath}");

            // Remove from main RepositoryRegistry too
            var app = (App)Application.Current!;
            app.RepositoryRegistry.Remove(repo.LocalPath);

            repo.LocalPath = null;
            repo.IsLocal = false;
            repo.UncommittedCount = 0;
            repo.RefreshStatus();

            if (!repo.IsRemote)
            {
                // Local-only repo: remove from list entirely
                _allRepos.Remove(repo);
            }

            StatusBarText.Text = $"Deleted {repo.Name}.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnDeleteLocal_Click FAILED: {ex.Message}");
            StatusBarText.Text = $"Delete failed: {ex.Message}";
        }
    }

    private void BtnRepoAction_Click(object? sender, RoutedEventArgs e)
    {
        // Inline "Clone" button in the repo row for remote-only repos
        FileLog.Write("[RepositoryManagerDialog] BtnRepoAction_Click: entered");
        try
        {
            if (sender is not Button { Tag: MergedRepoItem repo }) return;

            if (!repo.IsLocal && repo.IsRemote)
            {
                // Select this item and trigger clone
                RepoList.SelectedItem = repo;
                BtnCloneSelected_Click(sender, e);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryManagerDialog] BtnRepoAction_Click FAILED: {ex.Message}");
        }
    }

    // -- View Model ---------------------------------------------------------------

    internal class MergedRepoItem
    {
        private static readonly ISolidColorBrush CleanBg = new SolidColorBrush(Color.Parse("#1B4B2A"));
        private static readonly ISolidColorBrush CleanFg = new SolidColorBrush(Color.Parse("#3FB950"));
        private static readonly ISolidColorBrush UncommittedBg = new SolidColorBrush(Color.Parse("#6E4000"));
        private static readonly ISolidColorBrush UncommittedFg = new SolidColorBrush(Color.Parse("#FFD33D"));
        private static readonly ISolidColorBrush NotClonedBg = new SolidColorBrush(Color.Parse("#333333"));
        private static readonly ISolidColorBrush NotClonedFg = new SolidColorBrush(Color.Parse("#999999"));
        private static readonly ISolidColorBrush LocalOnlyBg = new SolidColorBrush(Color.Parse("#1B3A5C"));
        private static readonly ISolidColorBrush LocalOnlyFg = new SolidColorBrush(Color.Parse("#58A6FF"));
        private static readonly ISolidColorBrush CloneBtnBg = new SolidColorBrush(Color.Parse("#007ACC"));
        private static readonly ISolidColorBrush CloneBtnFg = new SolidColorBrush(Color.Parse("#FFFFFF"));
        private static readonly ISolidColorBrush TransparentBrush = new SolidColorBrush(Color.Parse("#00000000"));

        public string Name { get; set; } = "";
        public string? LocalPath { get; set; }
        public string? RemoteUrl { get; set; }
        public string RootLabel { get; set; } = "";
        public string RootPath { get; set; } = "";
        public bool IsLocal { get; set; }
        public bool IsRemote { get; set; }
        public int UncommittedCount { get; set; }
        public string? Description { get; set; }
        public bool IsPrivate { get; set; }

        // Display properties
        public string Subtitle => IsLocal ? (LocalPath ?? "") : (Description ?? RemoteUrl ?? "");
        public string WhereText => (IsLocal, IsRemote) switch
        {
            (true, true) => "both",
            (true, false) => "local",
            (false, true) => "remote",
            _ => ""
        };

        public string StatusText { get; private set; } = "";
        public ISolidColorBrush StatusBackground { get; private set; } = NotClonedBg;
        public ISolidColorBrush StatusForeground { get; private set; } = NotClonedFg;

        public string ActionText => (!IsLocal && IsRemote) ? "Clone" : "";
        public ISolidColorBrush ActionBackground => (!IsLocal && IsRemote) ? CloneBtnBg : TransparentBrush;
        public ISolidColorBrush ActionForeground => (!IsLocal && IsRemote) ? CloneBtnFg : TransparentBrush;
        public bool ActionIsVisible => !IsLocal && IsRemote;

        public MergedRepoItem()
        {
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            if (!IsLocal && IsRemote)
            {
                StatusText = "not cloned";
                StatusBackground = NotClonedBg;
                StatusForeground = NotClonedFg;
            }
            else if (IsLocal && !IsRemote)
            {
                StatusText = UncommittedCount > 0 ? $"{UncommittedCount} uncommitted" : "local only";
                StatusBackground = UncommittedCount > 0 ? UncommittedBg : LocalOnlyBg;
                StatusForeground = UncommittedCount > 0 ? UncommittedFg : LocalOnlyFg;
            }
            else if (IsLocal)
            {
                StatusText = UncommittedCount > 0 ? $"{UncommittedCount} uncommitted" : "clean";
                StatusBackground = UncommittedCount > 0 ? UncommittedBg : CleanBg;
                StatusForeground = UncommittedCount > 0 ? UncommittedFg : CleanFg;
            }
            else
            {
                StatusText = "";
                StatusBackground = NotClonedBg;
                StatusForeground = NotClonedFg;
            }
        }
    }
}
