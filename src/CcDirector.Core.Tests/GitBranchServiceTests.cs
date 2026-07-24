using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>The pure safe-delete verdict matrix - the same fail-closed shape as worktrees.</summary>
public class BranchSafetyEvaluatorTests
{
    [Fact]
    public void Current_NeverSafe_EvenWhenMerged()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(
            isCurrent: true, checkedOutInWorktree: false, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
    }

    [Fact]
    public void CheckedOutInWorktree_NeverSafe_EvenWhenMerged()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(
            isCurrent: false, checkedOutInWorktree: true, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
        Assert.Contains("worktree", why);
    }

    [Fact]
    public void InspectionFailed_NeverSafe()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, inspectionSucceeded: false, true, true, true);
        Assert.False(safe);
    }

    [Theory]
    [InlineData(true, false, false)]  // pull request merged
    [InlineData(false, true, false)]  // origin branch gone
    [InlineData(false, false, true)]  // contained in main
    public void AnySingleMergeSignal_IsSufficient(bool pr, bool gone, bool contained)
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, true, pr, gone, contained);
        Assert.True(safe);
    }

    [Fact]
    public void NoSignal_NotSafe_FailClosed()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(false, false, true, false, false, false);
        Assert.False(safe);
        Assert.Contains("not proven", why);
    }
}

/// <summary>Real-git integration: listing verdicts and the delete-time re-verify.</summary>
public sealed class GitBranchServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _repo;

    public GitBranchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-branch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "repo");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _repo);
        RunGit(_repo, "config", "user.email", "test@cc-director.local");
        RunGit(_repo, "config", "user.name", "CC Director Test");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        WriteFile("README.md", "init\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "initial");
        RunGit(_repo, "branch", "-M", "main");
        RunGit(_repo, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    [Fact]
    public async Task List_MergedBranch_SafeToDelete_UnmergedBranch_NotSafe()
    {
        // merged: commit on branch, push, fast-forward main, keep origin branch (contained-in-main path)
        RunGit(_repo, "checkout", "-b", "merged");
        WriteFile("m.txt", "m\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "merged work");
        RunGit(_repo, "push", "-u", "origin", "merged");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "merged");
        RunGit(_repo, "push", "origin", "main");

        // unmerged: a commit main does not have
        RunGit(_repo, "checkout", "-b", "unmerged");
        WriteFile("u.txt", "u\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work");
        RunGit(_repo, "checkout", "main");

        var branches = await new GitBranchService().ListAsync(_repo);

        var merged = Assert.Single(branches, b => b.Name == "merged");
        Assert.True(merged.SafeToDelete);

        var unmerged = Assert.Single(branches, b => b.Name == "unmerged");
        Assert.False(unmerged.SafeToDelete);
        Assert.Equal(1, unmerged.AheadOfMain);

        var main = Assert.Single(branches, b => b.Name == "main");
        Assert.True(main.IsCurrent);
        Assert.False(main.SafeToDelete);
    }

    [Fact]
    public async Task Delete_RefusesCurrent_AndBranchHeldByAWorktree()
    {
        // held: checked out in a linked worktree
        RunGit(_repo, "branch", "held");
        RunGit(_repo, "worktree", "add", Path.Combine(_root, "wt-held"), "held");

        var svc = new GitBranchService();

        var (deletedCurrent, whyCurrent) = await svc.DeleteIfSafeAsync(_repo, "main");
        Assert.False(deletedCurrent);
        Assert.Contains("branch you are on", whyCurrent);

        var (deletedHeld, whyHeld) = await svc.DeleteIfSafeAsync(_repo, "held");
        Assert.False(deletedHeld);
        Assert.Contains("worktree", whyHeld);

        // Both still exist.
        var names = (await svc.ListAsync(_repo)).Select(b => b.Name).ToList();
        Assert.Contains("main", names);
        Assert.Contains("held", names);
    }

    [Fact]
    public async Task Delete_SafeBranch_Deletes_AndUnmergedIsRefused()
    {
        RunGit(_repo, "checkout", "-b", "done");
        WriteFile("d.txt", "d\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "done work");
        RunGit(_repo, "push", "-u", "origin", "done");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "done");
        RunGit(_repo, "push", "origin", "main");

        RunGit(_repo, "checkout", "-b", "keep");
        WriteFile("k.txt", "k\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "keep work");
        RunGit(_repo, "checkout", "main");

        var svc = new GitBranchService();

        var (deleted, msg) = await svc.DeleteIfSafeAsync(_repo, "done");
        Assert.True(deleted, msg);

        var (refused, why) = await svc.DeleteIfSafeAsync(_repo, "keep");
        Assert.False(refused);
        Assert.Contains("not proven", why);

        var names = (await svc.ListAsync(_repo)).Select(b => b.Name).ToList();
        Assert.DoesNotContain("done", names);
        Assert.Contains("keep", names);
    }

    private void WriteFile(string rel, string content)
        => File.WriteAllText(Path.Combine(_repo, rel), content);

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
