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

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F3): deletion binds to the verified tip. When the branch
    // moves between the verdict and the delete (a concurrent commit), git refuses and the
    // branch - including the new commit - survives.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_WithAStaleVerifiedTip_IsRefused_AndTheBranchSurvives()
    {
        RunGit(_repo, "checkout", "-b", "moving");
        WriteFile("m1.txt", "one\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "first commit");
        var verifiedTip = RunGit(_repo, "rev-parse", "moving").Trim();

        // The concurrent commit: the branch tip moves AFTER the verdict was computed.
        WriteFile("m2.txt", "two\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "commit that arrived after verification");
        RunGit(_repo, "checkout", "main");

        var svc = new GitBranchService();
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "moving", verifiedTip, "test");

        Assert.False(deleted);
        Assert.Contains("moved since it was verified", message);
        var branches = await svc.ListAsync(_repo);
        var survivor = Assert.Single(branches, b => b.Name == "moving");
        Assert.NotEqual(verifiedTip, survivor.TipCommit); // the newer commit is intact
    }

    // ---------------------------------------------------------------------------------------
    // The success path of the atomic delete also cleans up the branch's config section.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_SafeBranch_RemovesTheBranchConfigSection()
    {
        RunGit(_repo, "checkout", "-b", "tidy");
        WriteFile("t.txt", "t\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "tidy work");
        RunGit(_repo, "push", "-u", "origin", "tidy"); // -u writes branch.tidy.remote and .merge
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "tidy");
        RunGit(_repo, "push", "origin", "main");

        var svc = new GitBranchService();
        var (deleted, msg) = await svc.DeleteIfSafeAsync(_repo, "tidy");
        Assert.True(deleted, msg);

        Assert.DoesNotContain("tidy", (await svc.ListAsync(_repo)).Select(b => b.Name));
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "config", "--get", "branch.tidy.merge"));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F2): C2 must test the CONFIGURED upstream, not
    // origin/<local-name>. A branch tracking a differently named upstream ref that still
    // exists must NOT be ruled origin-gone (before the fix it was, and was deletable).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_TrackingDifferentlyNamedUpstream_ThatStillExists_IsNotSafe()
    {
        RunGit(_repo, "checkout", "-b", "local-name");
        WriteFile("x.txt", "x\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work tracked under a different upstream name");
        RunGit(_repo, "push", "origin", "local-name:refs/heads/remote-name");
        RunGit(_repo, "config", "branch.local-name.remote", "origin");
        RunGit(_repo, "config", "branch.local-name.merge", "refs/heads/remote-name");
        RunGit(_repo, "checkout", "main");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "local-name");
        Assert.False(branch.SafeToDelete, branch.Explanation);

        var (deleted, _) = await new GitBranchService().DeleteIfSafeAsync(_repo, "local-name");
        Assert.False(deleted);
        Assert.Contains("local-name", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    // ---------------------------------------------------------------------------------------
    // The counterpart: when the CONFIGURED upstream (a differently named ref) genuinely was
    // deleted on the remote, the branch IS safe via the upstream-gone signal.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_WhoseConfiguredUpstreamWasDeleted_IsSafe()
    {
        RunGit(_repo, "checkout", "-b", "was-merged");
        WriteFile("y.txt", "y\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "work later squash merged and its upstream deleted");
        RunGit(_repo, "push", "origin", "was-merged:refs/heads/upstream-name");
        RunGit(_repo, "config", "branch.was-merged.remote", "origin");
        RunGit(_repo, "config", "branch.was-merged.merge", "refs/heads/upstream-name");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "push", "origin", "--delete", "upstream-name");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "was-merged");
        Assert.True(branch.SafeToDelete, branch.Explanation);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-7): git permits MULTIPLE branch.<name>.merge
    // values (an octopus pull), and "git config --get" silently returns only the last one - so
    // the probe could rule "upstream gone" while ANOTHER configured merge ref still exists on
    // the remote. A multi-valued merge configuration is ambiguous and must fail closed: the
    // branch is simply not eligible for the origin-gone signal, same as a missing value.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_WithTwoConfiguredMergeValues_IsNeverRuledSafeViaUpstreamGone()
    {
        RunGit(_repo, "checkout", "-b", "octopus");
        WriteFile("o.txt", "o\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work with two configured merge refs");
        RunGit(_repo, "push", "origin", "octopus:refs/heads/first-upstream");
        RunGit(_repo, "push", "origin", "octopus:refs/heads/second-upstream");
        RunGit(_repo, "config", "branch.octopus.remote", "origin");
        RunGit(_repo, "config", "branch.octopus.merge", "refs/heads/first-upstream");
        RunGit(_repo, "config", "--add", "branch.octopus.merge", "refs/heads/second-upstream");
        RunGit(_repo, "checkout", "main");

        // The value "--get" would select (the LAST one) is deleted on the remote; the OTHER
        // configured merge ref survives. Before the fix this was ruled origin-gone and safe.
        RunGit(_repo, "push", "origin", "--delete", "second-upstream");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "octopus");
        Assert.False(branch.SafeToDelete, branch.Explanation);

        var (deleted, _) = await new GitBranchService().DeleteIfSafeAsync(_repo, "octopus");
        Assert.False(deleted);
        Assert.Contains("octopus", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-1): "git update-ref -d" binds the tip but does
    // not reproduce "git branch -D"'s refusal to delete a branch that is checked out. When a
    // checkout into a linked worktree lands between verification and deletion, the delete must
    // be compensated: the ref is restored at the verified sha (lossless - we hold the exact
    // commit) and the worktree's HEAD stays valid.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_BranchCheckedOutInAWorktreeAfterVerification_IsRestored_AndTheWorktreeHeadStaysValid()
    {
        RunGit(_repo, "branch", "raced");
        var verifiedTip = RunGit(_repo, "rev-parse", "raced").Trim();

        // The race: AFTER the verdict was computed, another process checks the branch out into
        // a linked worktree - the tip is unchanged, so the tip-bound delete alone would succeed
        // and leave this worktree with a broken symbolic HEAD.
        var wt = Path.Combine(_root, "wt-raced");
        RunGit(_repo, "worktree", "add", wt, "raced");

        var svc = new GitBranchService();
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "raced", verifiedTip, "test");

        Assert.False(deleted);
        Assert.Contains("checked out in a worktree", message);

        // The branch survived (or was restored) at exactly the verified commit.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/raced").Trim());

        // The worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-1): the compensation restore is CREATE-ONLY.
    // When another process recreates the branch at a NEW commit between the delete and the
    // restore, the restore must refuse (git's expected-old-value of forty zeros) and the
    // recreated tip must survive untouched - the old restore silently rewound it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_BranchRecreatedBetweenDeleteAndRestore_TheRecreatedTipSurvivesUntouched()
    {
        RunGit(_repo, "branch", "raced");
        var verifiedTip = RunGit(_repo, "rev-parse", "raced").Trim();

        // The commit the concurrent recreation will point at - different from the verified tip.
        WriteFile("r.txt", "r\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "the recreated branch's commit");
        var recreatedTip = RunGit(_repo, "rev-parse", "main").Trim();
        Assert.NotEqual(verifiedTip, recreatedTip);

        // Checked out into a worktree AFTER verification, so compensation decides on a restore.
        var wt = Path.Combine(_root, "wt-recreate");
        RunGit(_repo, "worktree", "add", wt, "raced");

        // The interleaved "other process": right after our delete succeeds, it recreates the
        // branch at the newer commit.
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            async () =>
            {
                var recreate = await new GitCommandRunner().RunAsync(
                    _repo, new[] { "update-ref", "refs/heads/raced", recreatedTip });
                Assert.True(recreate.Success, recreate.Error);
            });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "raced", verifiedTip, "test");

        // The concurrently recreated branch stands, at ITS tip - never rewound to the verified
        // sha - and the outcome says plainly what happened.
        Assert.Equal(recreatedTip, RunGit(_repo, "rev-parse", "refs/heads/raced").Trim());
        Assert.True(deleted, message);
        Assert.Contains("same name has since appeared", message);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-2): everything after the successful ref delete
    // is a NON-CANCELLABLE compensation phase. Cancellation landing right after the delete
    // must not skip the worktree check and the restore - that left a checked-out worktree
    // with a broken symbolic HEAD. The branch must end restored here (a worktree holds it).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_CancelledRightAfterTheDeleteSucceeds_StillRestoresTheWorktreeHeldBranch()
    {
        RunGit(_repo, "branch", "cancel-race");
        var verifiedTip = RunGit(_repo, "rev-parse", "cancel-race").Trim();

        // Checked out into a worktree AFTER verification - compensation must restore.
        var wt = Path.Combine(_root, "wt-cancel");
        RunGit(_repo, "worktree", "add", wt, "cancel-race");

        // The caller's token is cancelled the instant the delete succeeds.
        using var cts = new CancellationTokenSource();
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => { cts.Cancel(); return Task.CompletedTask; });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "cancel-race", verifiedTip, "test", cts.Token);

        Assert.False(deleted);
        Assert.Contains("worktree", message);

        // Never deleted-with-broken-worktree: the branch is back at the verified commit and
        // the worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/cancel-race").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // The other legal end state under ruling R3-2: no worktree holds the branch, so a
    // cancellation right after the delete still lets compensation run to completion and the
    // branch ends CLEANLY deleted (worktree listing, ref check, and config cleanup all ran).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_CancelledRightAfterTheDeleteSucceeds_NoWorktree_EndsCleanlyDeleted()
    {
        RunGit(_repo, "checkout", "-b", "cancel-clean");
        WriteFile("cc.txt", "cc\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "cancel clean work");
        RunGit(_repo, "push", "-u", "origin", "cancel-clean"); // -u writes branch config to clean up
        RunGit(_repo, "checkout", "main");
        var verifiedTip = RunGit(_repo, "rev-parse", "cancel-clean").Trim();

        using var cts = new CancellationTokenSource();
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => { cts.Cancel(); return Task.CompletedTask; });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "cancel-clean", verifiedTip, "test", cts.Token);

        Assert.True(deleted, message);
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "rev-parse", "--verify", "refs/heads/cancel-clean"));
        // The compensation phase finished: the branch's config section was cleaned up too.
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "config", "--get", "branch.cancel-clean.merge"));
    }

    /// <summary>
    /// A git runner that fires an interleaved action right after the first command matching
    /// <c>match</c> succeeds - the deterministic stand-in for "another process acted between
    /// two of our git commands".
    /// </summary>
    private sealed class InterleavingGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Task> _interleaved;
        private int _fired;

        public InterleavingGitRunner(Func<string[], bool> match, Func<Task> interleaved)
        {
            _match = match;
            _interleaved = interleaved;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            var result = await base.RunAsync(workingDirectory, args, ct);
            if (result.Success && _match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                await _interleaved();
            return result;
        }
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
