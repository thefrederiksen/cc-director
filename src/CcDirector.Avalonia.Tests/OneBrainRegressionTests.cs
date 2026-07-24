using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The one-brain rule (issue #510 phase A): the per-session Source Control Worktrees tab and the
/// Repositories home render the SAME monitor model. These tests drive the per-session WorktreesView
/// from a monitor and assert it shows the monitor's worktrees - including when the session sits in
/// a WORKTREE of the repo rather than the repo itself - and that provisional entries cannot be acted on.
/// </summary>
public class OneBrainRegressionTests
{
    private static WorktreeInfo Wt(string branch, WorktreeSafety safety, WorktreeSafetyReason reason) => new()
    {
        Path = $"/repo/wt-{branch}",
        Branch = branch,
        Safety = safety,
        Reason = reason,
        Explanation = "x",
        IsClean = true,
    };

    private static RepositoryMonitor MonitorWithRepo(string repoPath, params WorktreeInfo[] worktrees)
        => new(
            enumerate: _ => new[] { repoPath },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p,
                Name = "repo",
                IsClean = true,
                Worktrees = worktrees,
                WorktreesSafeToReap = System.Linq.Enumerable.Count(worktrees, w => w.Safety == WorktreeSafety.SafeToReap),
                WorktreeCount = worktrees.Length,
                Success = true,
            }));

    [AvaloniaFact]
    public async Task SessionTab_RendersTheMonitorsWorktrees_ForTheRepoPath()
    {
        var monitor = MonitorWithRepo("/repo",
            Wt("done", WorktreeSafety.SafeToReap, WorktreeSafetyReason.OriginBranchGone),
            Wt("busy", WorktreeSafety.NeedsAttention, WorktreeSafetyReason.NotProvenMerged));
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new WorktreesView();
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        view.Attach(monitor, "/repo");
        Dispatcher.UIThread.RunJobs();

        // Same numbers the Repositories home computes from the same entry.
        Assert.Equal(1, view.OrphanedCount);
    }

    [AvaloniaFact]
    public async Task SessionTab_FindsTheRepo_WhenTheSessionSitsInAWorktree()
    {
        var wt = Wt("feature", WorktreeSafety.SafeToReap, WorktreeSafetyReason.ContainedInMain);
        var monitor = MonitorWithRepo("/repo", wt);
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new WorktreesView();
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        view.Attach(monitor, wt.Path); // the session lives IN the worktree
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.OrphanedCount); // resolved to the owning repository's entry
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F4): the reaper must target the repository ENTRY path,
    // never the session's own folder (which can be a linked worktree of that repository).
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task Reap_FromASessionSittingInAWorktree_TargetsThePrimaryRepositoryPath()
    {
        var wt = Wt("feature", WorktreeSafety.SafeToReap, WorktreeSafetyReason.ContainedInMain);
        var monitor = MonitorWithRepo("/repo", wt);
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new WorktreesView();
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        view.Attach(monitor, wt.Path); // the session lives IN the worktree
        Dispatcher.UIThread.RunJobs();

        string? reapedPath = null;
        view.ReapServiceOverride = (path, _) =>
        {
            reapedPath = path;
            return Task.FromResult(new ReapResult { Success = true });
        };
        await view.RunReapAsync();

        Assert.Equal("/repo", reapedPath); // the entry path - NOT the worktree the session sits in
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F4): while the owning entry is unresolved there is nothing
    // safe to reap - the old fallback to the raw session path could hand the reaper a linked
    // worktree.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task Reap_WhenTheOwningEntryIsUnresolved_DoesNothing()
    {
        var monitor = MonitorWithRepo("/repo");
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new WorktreesView();
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        view.Attach(monitor, "/somewhere/unrelated"); // no entry resolves for this path
        Dispatcher.UIThread.RunJobs();

        string? reapedPath = null;
        view.ReapServiceOverride = (path, _) =>
        {
            reapedPath = path;
            return Task.FromResult(new ReapResult { Success = true });
        };
        await view.RunReapAsync();

        Assert.Null(reapedPath); // the reaper was never invoked
    }

    [AvaloniaFact]
    public async Task SessionTab_UpdatesLive_WhenTheMonitorRecomputes()
    {
        // A real folder with a .git marker so RecomputeOneAsync takes the recompute path, not the
        // it-is-gone removal path.
        var repoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-onebrain-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(repoDir, ".git"));
        try
        {
            var worktrees = new List<WorktreeInfo> { Wt("done", WorktreeSafety.SafeToReap, WorktreeSafetyReason.OriginBranchGone) };
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { repoDir },
                compute: (p, _, _) => Task.FromResult(new RepositoryStatus
                {
                    Path = p, Name = "repo", IsClean = true,
                    Worktrees = worktrees.ToArray(),
                    WorktreesSafeToReap = System.Linq.Enumerable.Count(worktrees, w => w.Safety == WorktreeSafety.SafeToReap),
                    WorktreeCount = worktrees.Count,
                    Success = true,
                }));
            await monitor.RescanAsync(new[] { "/roots" });

            var view = new WorktreesView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();
            view.Attach(monitor, repoDir);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, view.OrphanedCount);

            // The worktree gets reaped elsewhere; the monitor recomputes; the tab follows - no rescan here.
            worktrees.Clear();
            await monitor.RecomputeOneAsync(repoDir);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, view.OrphanedCount);
        }
        finally
        {
            try { System.IO.Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }
}
