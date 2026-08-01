using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Setup;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Tools catalog page: lists every cc-* tool with a built/PASS/FAIL chip, and a detail pane
/// (Overview / Commands / Tests / Skills / Logs) for the selected tool. Reads the Core
/// <see cref="ToolCatalogService"/>, runs checks via <see cref="ToolTestRunner"/>, and shows skill
/// links from <see cref="SkillToolLinker"/> - the same Core surface the Control API exposes.
///
/// Responsive-UI rule: the catalog loads asynchronously on first show; tests run off the UI thread
/// and the status chips update via INotifyPropertyChanged.
/// </summary>
public partial class ToolsView : UserControl
{
    private readonly ToolCatalogService _catalog = new();
    private readonly ToolTestRunner _runner = new();
    private readonly SkillToolLinker _linker = new();

    private readonly List<ToolItemViewModel> _allItems = new();
    private IReadOnlyList<SkillToolLink> _allLinks = Array.Empty<SkillToolLink>();
    private bool _loaded;

    // Supplied by the owning window with the fault verdict, so the banner re-checks against the same
    // Director the verdict was reached about.
    private string? _controlApiBaseUrl;
    private string? _expectedBinDir;
    private Func<Task>? _onFleetToolRepaired;

    public ToolsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadCatalogAsync();
    }

    /// <summary>Reload the catalog and re-run the checks. Called after a repair changes what is
    /// installed (the Settings Tools tab hosts this view above its "Download and repair tools" button),
    /// so the status list reflects the freshly installed toolset without reopening the dialog.</summary>
    public Task ReloadAsync() => LoadCatalogAsync();

    /// <summary>
    /// Show (or hide) the PATH fault banner from a verdict the Director already reached.
    ///
    /// The verdict is PASSED IN rather than re-derived here, so the badge on the rail and this banner
    /// can never disagree about the same machine at the same moment - the disagreement between two
    /// surfaces describing one thing is the failure this whole area exists to prevent.
    /// </summary>
    /// <param name="check">The Director's latest reachability verdict, or null when it has none yet.
    /// Null hides the banner: no verdict is not a fault, and it is not a pass either.</param>
    /// <param name="controlApiBaseUrl">The Director's own Control API address, so the banner can
    /// re-check against the same endpoint after a repair.</param>
    /// <param name="onRepaired">Invoked after a successful repair so the owning window re-drives its
    /// own badge rather than being left showing a fault the user has just fixed.</param>
    public void ShowFleetToolStatus(
        FleetToolCheck? check, string? controlApiBaseUrl, Func<Task>? onRepaired = null)
    {
        _controlApiBaseUrl = controlApiBaseUrl;
        _onFleetToolRepaired = onRepaired;
        RenderFleetToolStatus(check);
    }

    private void RenderFleetToolStatus(FleetToolCheck? check)
    {
        if (check is not { Verdict: FleetToolVerdict.CannotReachDirector })
        {
            PathFaultBanner.IsVisible = false;
            return;
        }

        PathFaultBanner.IsVisible = true;
        _expectedBinDir = check.ExpectedBinDir;
        PathFaultResolved.Text = check.ResolvedPath ?? "(not resolved)";
        PathFaultExpected.Text = check.ExpectedBinDir ?? "(unknown)";
        PathFaultExplanation.Text = check.IsDifferentInstall
            ? "The command line on your PATH belongs to another install, so agents in your sessions "
              + "report \"cannot connect to DevThrottle\" even though this Director is healthy and connected."
            // Same install, still refused: repointing PATH will not help, so say what was actually seen
            // rather than offering a fix for a fault this is not.
            : $"The command line on your PATH could not authenticate against this Director: {check.Detail}";

        // Only offer the button for the fault it actually repairs.
        PathFaultFixButton.IsVisible = check.IsDifferentInstall;
        PathFaultProgress.Text = "";
    }

    private async void PathFaultFixButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Immediate feedback before any awaited work (responsive-UI rule).
            PathFaultFixButton.IsEnabled = false;
            PathFaultProgress.Text = "Repointing PATH...";

            // The directory the verdict named as ours. Re-deriving it here from a different source is how
            // the banner and the check come to disagree about which directory the repair is even for.
            var binDir = _expectedBinDir;
            if (string.IsNullOrWhiteSpace(binDir))
            {
                PathFaultProgress.Text = "No install directory to repoint to.";
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            var repair = await Task.Run(() => FleetToolPathRepair.PutFirstOnPath(binDir));

            if (!repair.Succeeded)
            {
                PathFaultProgress.Text = repair.Detail;
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            // Re-ask the question rather than assuming the repair worked. A button that reports its own
            // success without re-checking is how a fix that did nothing still looks like a fix.
            if (string.IsNullOrWhiteSpace(_controlApiBaseUrl))
            {
                PathFaultProgress.Text = "PATH updated. Re-open Settings to re-check.";
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            PathFaultProgress.Text = "Checking...";
            var recheck = await new FleetToolReachability().RunAsync(_controlApiBaseUrl, binDir);
            RenderFleetToolStatus(recheck);

            if (recheck.Verdict == FleetToolVerdict.Working)
            {
                if (_onFleetToolRepaired is { } notify) await notify();
            }
            else
            {
                PathFaultProgress.Text = $"PATH updated, but it still cannot reach this Director: {recheck.Detail}";
                PathFaultFixButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] PathFaultFixButton_Click FAILED: {ex.Message}");
            PathFaultProgress.Text = $"Could not repoint PATH: {ex.Message}";
            PathFaultFixButton.IsEnabled = true;
        }
    }

    private async Task LoadCatalogAsync()
    {
        FileLog.Write("[ToolsView] LoadCatalogAsync");
        try
        {
            ListSummary.Text = "Loading...";

            var (descriptors, links, unmanaged) = await Task.Run(() =>
            {
                var d = _catalog.GetCatalog();
                var l = _linker.BuildLinks();
                var u = _catalog.GetUnmanagedBinaries();
                return (d, l, u);
            });

            _allLinks = links;
            _allItems.Clear();
            foreach (var d in descriptors)
                _allItems.Add(new ToolItemViewModel(d));

            ApplyFilter();

            // Availability (PATH or bundled bin), not bin-only IsBuilt, is the user-facing signal (issue #448).
            var available = _allItems.Count(i => i.IsAvailable);
            var unavailable = _allItems.Count - available;
            SummaryText.Text = $"{_allItems.Count} tools   {available} available   {unavailable} unavailable";

            var unmanagedNote = unmanaged.Count > 0
                ? $"\nUnmanaged binaries (not in manifest): {string.Join(", ", unmanaged)}"
                : "";
            ListSummary.Text = $"{available}/{_allItems.Count} available.{unmanagedNote}";

            // Auto-run the checks once so built tools show PASS/FAIL right away instead of a wall of
            // "untested" the user has to clear manually (the screenshot complaint). Fire-and-forget;
            // chips update live off the UI thread, and the "Run All Tests" button reflects progress.
            _ = RunAllAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] LoadCatalogAsync FAILED: {ex.Message}");
            ListSummary.Text = $"Failed to load catalog: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ToolItemViewModel> items = _allItems;
        if (query.Length > 0)
            items = _allItems.Where(i =>
                i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        ToolList.ItemsSource = items.ToList();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ToolList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm)
        {
            DetailPanel.IsVisible = false;
            DetailEmpty.IsVisible = true;
            return;
        }

        DetailEmpty.IsVisible = false;
        DetailPanel.IsVisible = true;
        PopulateDetail(vm);
    }

    private void PopulateDetail(ToolItemViewModel vm)
    {
        var d = vm.Descriptor;
        DetailName.Text = d.Name;
        DetailDescription.Text = d.Description;
        DetailCategory.Text = d.Category;
        DetailBinaryPath.Text = d.BinaryPath
            + (d.IsAvailable
                ? (d.IsBuilt ? "" : "   (on PATH)")
                : "   (unavailable)");
        DetailVersion.Text = "";

        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            DetailNote.Text = d.Note;
            DetailNoteBox.IsVisible = true;
        }
        else
        {
            DetailNoteBox.IsVisible = false;
        }

        UpdateStatusChip(vm.Status);

        // Reset per-tool dynamic panes.
        TestsList.ItemsSource = null;
        TestsHint.IsVisible = true;
        LogsOutput.Text = "";
        CommandsOutput.Text = "";

        // Skills for this tool.
        var links = _allLinks.Where(l => string.Equals(l.ToolName, d.Name, StringComparison.OrdinalIgnoreCase))
                             .Select(l => new SkillLinkViewModel(l)).ToList();
        SkillsList.ItemsSource = links;
        SkillsEmpty.IsVisible = links.Count == 0;
    }

    private void UpdateStatusChip(ToolStatus status)
    {
        var (label, brush) = ToolStatusVisuals.For(status);
        DetailStatusText.Text = label;
        DetailStatusChip.Background = brush;
    }

    /// <summary>
    /// Run the selected tool's checks (issue #1107, item 7).
    ///
    /// This is the whole problem in one file: RunAllAsync two methods below opens with an explicit
    /// re-entrancy guard and a label change, and this handler - which also spawns processes - had neither.
    /// Same screen, same file, two different standards.
    /// </summary>
    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm) return;
        if (sender is not Control button) return;

        await BusyAction.RunAsync(button, async () =>
        {
            await RunToolAsync(vm, refreshDetailIfSelected: true);
            UpdateSummary();
        }, "Running...", owner: TopLevel.GetTopLevel(this) as Window, failureTitle: "Could not run the tool");
    }

    private async void RunAllButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ToolsView] RunAllButton_Click");
        await RunAllAsync();
    }

    /// <summary>
    /// Run every tool's checks with bounded concurrency, updating the chips live. Used by the
    /// "Run All Tests" button AND auto-triggered once when the catalog first loads, so a built tool
    /// shows PASS/FAIL instead of a wall of "untested" the user must manually clear. Tools whose
    /// smoke command needs credentials declare no smoke test, so this is just their presence+version
    /// check; tools with a read-only smoke run that too. Re-entrancy is guarded by the button state.
    /// </summary>
    private async Task RunAllAsync()
    {
        if (!RunAllButton.IsEnabled) return; // a run is already in progress
        RunAllButton.IsEnabled = false;
        RunAllButton.Content = "Running...";
        try
        {
            // Bounded concurrency so we do not spawn 30 processes at once.
            using var gate = new System.Threading.SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
            var selected = ToolList.SelectedItem as ToolItemViewModel;

            var tasks = _allItems.Select(async vm =>
            {
                await gate.WaitAsync();
                try { await RunToolAsync(vm, refreshDetailIfSelected: ReferenceEquals(vm, selected)); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            RunAllButton.IsEnabled = true;
            RunAllButton.Content = "Run All Tests";
            UpdateSummary();
        }
    }

    private async Task RunToolAsync(ToolItemViewModel vm, bool refreshDetailIfSelected)
    {
        var d = vm.Descriptor;
        var results = await _runner.RunAllForToolAsync(d);

        var status = !d.IsAvailable
            ? ToolStatus.NotBuilt
            : results.All(r => r.Passed) ? ToolStatus.Pass : ToolStatus.Fail;
        vm.Status = status;

        if (refreshDetailIfSelected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatusChip(status);
                TestsHint.IsVisible = false;
                TestsList.ItemsSource = results.Select(r => new TestResultViewModel(r)).ToList();

                var version = results.FirstOrDefault(r => r.Kind == ToolTestKind.Version);
                if (version is { Passed: true })
                    DetailVersion.Text = version.Message;

                LogsOutput.Text = BuildLogText(d, results);
            });
        }
    }

    private static string BuildLogText(ToolDescriptor d, IReadOnlyList<ToolTestResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {d.Name}");
        sb.AppendLine($"# binary: {d.BinaryPath}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"=== {r.Label}  [{(r.Passed ? "PASS" : "FAIL")}]  exit={r.ExitCode?.ToString() ?? "n/a"}  {r.DurationMs}ms ===");
            if (!string.IsNullOrWhiteSpace(r.Stdout))
            {
                sb.AppendLine("--- stdout ---");
                sb.Append(r.Stdout);
            }
            if (!string.IsNullOrWhiteSpace(r.Stderr))
            {
                sb.AppendLine("--- stderr ---");
                sb.Append(r.Stderr);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void UpdateSummary()
    {
        var available = _allItems.Count(i => i.IsAvailable);
        var pass = _allItems.Count(i => i.Status == ToolStatus.Pass);
        var fail = _allItems.Count(i => i.Status == ToolStatus.Fail);
        var unavailable = _allItems.Count - available;
        SummaryText.Text = $"{_allItems.Count} tools   {pass} PASS   {fail} FAIL   {unavailable} UNAVAILABLE";
    }

    private async void LoadCommandsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm) return;
        var d = vm.Descriptor;
        if (!d.IsAvailable)
        {
            CommandsOutput.Text = "(tool unavailable - cannot read --help)";
            return;
        }

        CommandsOutput.Text = "Loading --help...";
        try
        {
            var help = await RunHelpAsync(d.BinaryPath);
            CommandsOutput.Text = string.IsNullOrWhiteSpace(help) ? "(no output)" : help;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] LoadCommands {d.Name} failed: {ex.Message}");
            CommandsOutput.Text = $"Failed to run --help: {ex.Message}";
        }
    }

    /// <summary>Run <c>&lt;tool&gt; --help</c> (read-only) and return the combined output.</summary>
    private static async Task<string> RunHelpAsync(string binaryPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(binaryPath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--help");

        // Event-based reads: reading both pipes to completion synchronously can deadlock when the
        // child fills one buffer while we block on the other.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return stdout + stderr.ToString() + "\n(timed out)";
        }

        var outText = stdout.ToString();
        var errText = stderr.ToString();
        return string.IsNullOrWhiteSpace(outText) ? errText : outText;
    }
}
