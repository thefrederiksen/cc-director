using CcDirector.ControlApi;
using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// REGRESSION (inspection finding F10): provisional (still-verifying) repositories fail closed at
/// the fold. Their worktrees are never served as "safe-to-reap" and their safe count folds to
/// zero, so cached, unverified data can never be counted as reclaimable by any client or the CLI.
/// </summary>
public class RepositoryDtoMapperTests
{
    private static RepositoryStatus StatusWithSafeWorktree(bool provisional) => new()
    {
        Path = @"D:\repos\widget",
        Name = "widget",
        Branch = "main",
        IsClean = true,
        WorktreeCount = 1,
        WorktreesSafeToReap = 1,
        Worktrees = new[]
        {
            new WorktreeInfo
            {
                Path = @"D:\repos\widget-wt",
                Branch = "done",
                Safety = WorktreeSafety.SafeToReap,
                Explanation = "Origin branch deleted after merge.",
                SizeBytes = 1024,
            },
        },
        Provisional = provisional,
        Success = true,
    };

    [Fact]
    public void Map_ProvisionalRepository_FoldsSafeCountToZero_AndWorktreesToVerifying()
    {
        var dto = RepositoryDtoMapper.Map(StatusWithSafeWorktree(provisional: true), "d1", "M1");

        Assert.True(dto.Provisional);
        Assert.Equal(0, dto.WorktreesSafeToReap);
        var wt = Assert.Single(dto.Worktrees);
        Assert.Equal("verifying", wt.State);
        Assert.DoesNotContain("safe", wt.State);
    }

    [Fact]
    public void Map_VerifiedRepository_KeepsTheSafeCountAndState()
    {
        var dto = RepositoryDtoMapper.Map(StatusWithSafeWorktree(provisional: false), "d1", "M1");

        Assert.False(dto.Provisional);
        Assert.Equal(1, dto.WorktreesSafeToReap);
        Assert.Equal("safe-to-reap", Assert.Single(dto.Worktrees).State);
    }

    [Fact]
    public void Flatten_ProvisionalRepository_ServesVerifying_EvenWhenThePushedStateSaysSafe()
    {
        // The shape an OLDER Director could push: Provisional set, but the worktree state string
        // still "safe-to-reap". The shared fold must fail closed at serve time.
        var pushed = new RepoStatusDto
        {
            Name = "widget",
            Path = @"D:\repos\widget",
            MachineName = "M1",
            DirectorId = "d1",
            Provisional = true,
            Worktrees = new List<WorktreeDto>
            {
                new() { Path = @"D:\repos\widget-wt", Branch = "done", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
            },
        };

        var row = Assert.Single(FleetWorktreeFold.Flatten(new[] { pushed }, dataAgeSeconds: 2));

        Assert.Equal("verifying", row.State);
        Assert.True(row.Provisional);
        Assert.Equal(2, row.DataAgeSeconds);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-3): GET /repositories serves through the SAME
    // repository-level fold as /worktrees. The shape a PRE-FIX Director can push - Provisional
    // set but a stale positive safe count and stale "safe-to-reap" worktree states - must fold
    // at serve time: safe count zero, worktrees "verifying". The Gateway owns the verdict.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void FoldRepositoryForServe_OldDirectorProvisionalShape_ServesZeroSafeCount_AndVerifyingWorktrees()
    {
        var pushedByOldDirector = new RepoStatusDto
        {
            Name = "widget",
            Path = @"D:\repos\widget",
            MachineName = "M1",
            DirectorId = "d1",
            Provisional = true,
            WorktreeCount = 2,
            WorktreesSafeToReap = 2, // the stale safe count a pre-fix Director pushes
            WorktreeBytes = 8192,
            Worktrees = new List<WorktreeDto>
            {
                new() { Path = @"D:\repos\widget-wt1", Branch = "done", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
                new() { Path = @"D:\repos\widget-wt2", Branch = "old", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
            },
        };

        var served = FleetWorktreeFold.FoldRepositoryForServe(pushedByOldDirector);

        Assert.True(served.Provisional);
        Assert.Equal(0, served.WorktreesSafeToReap); // the stale count never serves
        Assert.Equal(2, served.Worktrees.Count);
        Assert.All(served.Worktrees, w => Assert.Equal("verifying", w.State));
        Assert.All(served.Worktrees, w => Assert.DoesNotContain("safe", w.State));

        // The cached instance was never mutated - the fold serves a copy.
        Assert.Equal(2, pushedByOldDirector.WorktreesSafeToReap);
        Assert.Equal("safe-to-reap", pushedByOldDirector.Worktrees[0].State);
    }

    [Fact]
    public void FoldRepositoryForServe_VerifiedRepository_PassesThroughUnchanged()
    {
        var pushed = new RepoStatusDto
        {
            Name = "widget",
            Path = @"D:\repos\widget",
            Provisional = false,
            WorktreesSafeToReap = 1,
            Worktrees = new List<WorktreeDto>
            {
                new() { Path = @"D:\repos\widget-wt", Branch = "done", State = "safe-to-reap", Reason = "merged" },
            },
        };

        var served = FleetWorktreeFold.FoldRepositoryForServe(pushed);

        Assert.Same(pushed, served); // verified data serves as pushed
        Assert.Equal(1, served.WorktreesSafeToReap);
        Assert.Equal("safe-to-reap", Assert.Single(served.Worktrees).State);
    }

    [Fact]
    public void Flatten_VerifiedRepository_PassesTheFoldedStateThrough()
    {
        var pushed = new RepoStatusDto
        {
            Name = "widget",
            Path = @"D:\repos\widget",
            MachineName = "M1",
            DirectorId = "d1",
            Provisional = false,
            Worktrees = new List<WorktreeDto>
            {
                new() { Path = @"D:\repos\widget-wt", Branch = "done", State = "safe-to-reap", Reason = "merged" },
            },
        };

        var row = Assert.Single(FleetWorktreeFold.Flatten(new[] { pushed }));

        Assert.Equal("safe-to-reap", row.State);
        Assert.False(row.Provisional);
    }
}
