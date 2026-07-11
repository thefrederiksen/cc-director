using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #1266: the Director's GET /sessions/{sid}/git response is additively enriched with the per-file
/// staged/unstaged lists so the Cockpit's read-only Source Control tab can list what is changed. These
/// pin the enrichment: a repository with a staged file, an unstaged file, and an untracked file produces
/// the right paths and change kinds in the right lists, and the enrichment never disturbs the summary
/// fields the Wingman consumer relies on. The parsed status is produced by the same
/// <see cref="GitStatusProvider.ParsePorcelainOutput"/> the endpoint's provider uses, so this exercises
/// the real payload shape without a git subprocess.
/// </summary>
public sealed class GitChangeMapperTests
{
    [Fact]
    public void Enrich_StagedUnstagedAndUntracked_LandInTheRightListsWithTheirChangeKind()
    {
        // A repo with: one staged add, one unstaged modification, one untracked file - the exact scenario
        // the acceptance test describes, expressed as the porcelain output git would emit for it.
        var status = GitStatusProvider.ParsePorcelainOutput(
            "A  src/Added.cs\n M src/Modified.cs\n?? notes/untracked.txt\n");
        var snapshot = new GitSnapshot { Status = "ok", Branch = "main" };

        GitChangeMapper.Enrich(snapshot, status);

        var staged = Assert.Single(snapshot.StagedChanges);
        Assert.Equal("src/Added.cs", staged.Path);
        Assert.Equal("A", staged.ChangeKind);

        Assert.Equal(2, snapshot.UnstagedChanges.Count);
        var modified = snapshot.UnstagedChanges[0];
        Assert.Equal("src/Modified.cs", modified.Path);
        Assert.Equal("M", modified.ChangeKind);
        var untracked = snapshot.UnstagedChanges[1];
        Assert.Equal("notes/untracked.txt", untracked.Path);
        Assert.Equal("?", untracked.ChangeKind);
    }

    [Fact]
    public void Enrich_IsAdditive_LeavesTheSummaryFieldsUntouched()
    {
        // The Wingman consumer of GitSnapshotAsync reads the summary fields only; enrichment must never
        // rewrite them.
        var status = GitStatusProvider.ParsePorcelainOutput(" M file.cs\n");
        var snapshot = new GitSnapshot
        {
            Status = "ok",
            Branch = "feature/x",
            Dirty = true,
            Ahead = 2,
            Behind = 1,
            LastCommit = "a1b2c3d do a thing",
        };

        GitChangeMapper.Enrich(snapshot, status);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("feature/x", snapshot.Branch);
        Assert.True(snapshot.Dirty);
        Assert.Equal(2, snapshot.Ahead);
        Assert.Equal(1, snapshot.Behind);
        Assert.Equal("a1b2c3d do a thing", snapshot.LastCommit);
        Assert.Single(snapshot.UnstagedChanges);
    }

    [Fact]
    public void Enrich_CleanRepository_ProducesEmptyLists()
    {
        var status = GitStatusProvider.ParsePorcelainOutput("");
        var snapshot = new GitSnapshot { Status = "ok" };

        GitChangeMapper.Enrich(snapshot, status);

        Assert.Empty(snapshot.StagedChanges);
        Assert.Empty(snapshot.UnstagedChanges);
    }

    [Fact]
    public void Enrich_FileStagedAndUnstaged_AppearsInBothLists()
    {
        // "MM" - the same file has a staged change AND a further unstaged change; it must show in both.
        var status = GitStatusProvider.ParsePorcelainOutput("MM src/Both.cs\n");
        var snapshot = new GitSnapshot { Status = "ok" };

        GitChangeMapper.Enrich(snapshot, status);

        Assert.Equal("src/Both.cs", Assert.Single(snapshot.StagedChanges).Path);
        Assert.Equal("src/Both.cs", Assert.Single(snapshot.UnstagedChanges).Path);
    }
}
