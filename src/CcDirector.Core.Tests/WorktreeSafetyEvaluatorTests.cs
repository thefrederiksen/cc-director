using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Exhaustive, I/O-free tests of the fail-closed safety decision. These pin the exact rule
/// from issue #503: safe only if not-primary AND clean AND proven-merged; otherwise stranded.
/// </summary>
public class WorktreeSafetyEvaluatorTests
{
    private static WorktreeFacts Base() => new()
    {
        IsPrimary = false,
        IsDetachedHead = false,
        IsClean = true,
        PullRequestMerged = false,
        OriginBranchGone = false,
        ContainedInMain = false,
        DetachedHeadIsAncestorOfMain = false,
        InspectionSucceeded = true,
    };

    // --- Guardrail A: the primary checkout is never safe, whatever else is true ---

    [Fact]
    public void Primary_IsNeverSafe_EvenWhenCleanAndMerged()
    {
        var facts = Base() with
        {
            IsPrimary = true,
            PullRequestMerged = true,
            OriginBranchGone = true,
            ContainedInMain = true,
        };

        var v = WorktreeSafetyEvaluator.Evaluate(facts);

        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.PrimaryCheckout, v.Reason);
    }

    // --- Guardrail B: any content at all is never safe, even when merged ---

    [Fact]
    public void Dirty_IsNeverSafe_EvenWhenMerged()
    {
        var facts = new WorktreeFacts
        {
            IsClean = false,
            PullRequestMerged = true,
            OriginBranchGone = true,
            ContainedInMain = true,
            InspectionSucceeded = true,
        };

        var v = WorktreeSafetyEvaluator.Evaluate(facts);

        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.UncommittedChanges, v.Reason);
    }

    // --- Fail closed: a failed probe is never safe ---

    [Fact]
    public void InspectionFailed_IsNeverSafe()
    {
        var facts = new WorktreeFacts
        {
            IsClean = true,
            ContainedInMain = true,
            InspectionSucceeded = false,
        };

        var v = WorktreeSafetyEvaluator.Evaluate(facts);

        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.InspectionFailed, v.Reason);
    }

    // --- Each single merge signal is sufficient (C1, C2, C3) ---

    [Fact]
    public void CleanBranch_WithMergedPullRequest_IsSafe()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { PullRequestMerged = true });
        Assert.Equal(WorktreeSafety.SafeToReap, v.Safety);
        Assert.Equal(WorktreeSafetyReason.PullRequestMerged, v.Reason);
    }

    [Fact]
    public void CleanBranch_WithOriginBranchGone_IsSafe()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { OriginBranchGone = true });
        Assert.Equal(WorktreeSafety.SafeToReap, v.Safety);
        Assert.Equal(WorktreeSafetyReason.OriginBranchGone, v.Reason);
    }

    [Fact]
    public void CleanBranch_ContainedInMain_IsSafe()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { ContainedInMain = true });
        Assert.Equal(WorktreeSafety.SafeToReap, v.Safety);
        Assert.Equal(WorktreeSafetyReason.ContainedInMain, v.Reason);
    }

    // --- No merge signal: stranded ---

    [Fact]
    public void CleanBranch_WithNoMergeSignal_IsStranded()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base());
        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, v.Reason);
    }

    // --- Detached HEAD: safe only if it is an ancestor of origin/main ---

    [Fact]
    public void DetachedHead_AncestorOfMain_IsSafe()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with
        {
            IsDetachedHead = true,
            DetachedHeadIsAncestorOfMain = true,
        });
        Assert.Equal(WorktreeSafety.SafeToReap, v.Safety);
        Assert.Equal(WorktreeSafetyReason.DetachedHeadAncestorOfMain, v.Reason);
    }

    [Fact]
    public void DetachedHead_NotAncestorOfMain_IsStranded()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with
        {
            IsDetachedHead = true,
            DetachedHeadIsAncestorOfMain = false,
        });
        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, v.Reason);
    }

    [Fact]
    public void DetachedHead_Dirty_IsStranded_RegardlessOfAncestry()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with
        {
            IsDetachedHead = true,
            DetachedHeadIsAncestorOfMain = true,
            IsClean = false,
        });
        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.UncommittedChanges, v.Reason);
    }

    // --- Third case: git-safe but a live session is open in the worktree ---

    [Fact]
    public void CleanBranch_ThatWouldBeSafe_WithLiveSession_IsInUseNotSafe()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { OriginBranchGone = true, HasLiveSession = true });
        Assert.Equal(WorktreeSafety.InUseBySession, v.Safety);
        Assert.Equal(WorktreeSafetyReason.LiveSessionOpen, v.Reason);
    }

    [Fact]
    public void ContainedInMain_WithLiveSession_IsInUse()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { ContainedInMain = true, HasLiveSession = true });
        Assert.Equal(WorktreeSafety.InUseBySession, v.Safety);
    }

    [Fact]
    public void DetachedHead_AncestorOfMain_WithLiveSession_IsInUse()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with
        {
            IsDetachedHead = true,
            DetachedHeadIsAncestorOfMain = true,
            HasLiveSession = true,
        });
        Assert.Equal(WorktreeSafety.InUseBySession, v.Safety);
        Assert.Equal(WorktreeSafetyReason.LiveSessionOpen, v.Reason);
    }

    [Fact]
    public void Dirty_WithLiveSession_StaysNeedsAttention_NotInUse()
    {
        // A dirty worktree was never safe; a live session does not change that.
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { IsClean = false, HasLiveSession = true });
        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.UncommittedChanges, v.Reason);
    }

    [Fact]
    public void StrandedBranch_WithLiveSession_StaysNeedsAttention()
    {
        var v = WorktreeSafetyEvaluator.Evaluate(Base() with { HasLiveSession = true });
        Assert.Equal(WorktreeSafety.NeedsAttention, v.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, v.Reason);
    }
}
