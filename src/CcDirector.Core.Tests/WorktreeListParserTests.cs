using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class WorktreeListParserTests
{
    [Fact]
    public void Parse_SingleBranchWorktree_ExtractsPathHeadAndShortBranch()
    {
        var porcelain =
            "worktree /home/user/repo\n" +
            "HEAD 1111111111111111111111111111111111111111\n" +
            "branch refs/heads/main\n";

        var entries = WorktreeListParser.Parse(porcelain);

        var e = Assert.Single(entries);
        Assert.Equal("/home/user/repo", e.Path);
        Assert.Equal("1111111111111111111111111111111111111111", e.Head);
        Assert.Equal("main", e.Branch);
        Assert.False(e.IsDetached);
        Assert.False(e.IsBare);
    }

    [Fact]
    public void Parse_MultipleWorktrees_KeepsOrderMainFirst()
    {
        var porcelain =
            "worktree /repo/main\n" +
            "HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repo/feature\n" +
            "HEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n" +
            "branch refs/heads/feature-x\n";

        var entries = WorktreeListParser.Parse(porcelain);

        Assert.Equal(2, entries.Count);
        Assert.Equal("/repo/main", entries[0].Path);
        Assert.Equal("main", entries[0].Branch);
        Assert.Equal("/repo/feature", entries[1].Path);
        Assert.Equal("feature-x", entries[1].Branch);
    }

    [Fact]
    public void Parse_DetachedHead_HasNoBranchAndIsFlaggedDetached()
    {
        var porcelain =
            "worktree /repo/detached\n" +
            "HEAD cccccccccccccccccccccccccccccccccccccccc\n" +
            "detached\n";

        var e = Assert.Single(WorktreeListParser.Parse(porcelain));
        Assert.Null(e.Branch);
        Assert.True(e.IsDetached);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", e.Head);
    }

    [Fact]
    public void Parse_BareRepository_IsFlaggedBare()
    {
        var porcelain =
            "worktree /repo/bare\n" +
            "bare\n";

        var e = Assert.Single(WorktreeListParser.Parse(porcelain));
        Assert.True(e.IsBare);
    }

    [Fact]
    public void Parse_CrlfLineEndings_AreHandled()
    {
        var porcelain =
            "worktree /repo/main\r\n" +
            "HEAD dddddddddddddddddddddddddddddddddddddddd\r\n" +
            "branch refs/heads/main\r\n";

        var e = Assert.Single(WorktreeListParser.Parse(porcelain));
        Assert.Equal("/repo/main", e.Path);
        Assert.Equal("main", e.Branch);
    }

    [Fact]
    public void Parse_EmptyOrNullInput_ReturnsNoEntries()
    {
        Assert.Empty(WorktreeListParser.Parse(""));
        Assert.Empty(WorktreeListParser.Parse(null!));
    }

    [Fact]
    public void Parse_BranchWithSlashes_KeepsFullShortName()
    {
        var porcelain =
            "worktree /repo/feat\n" +
            "HEAD eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\n" +
            "branch refs/heads/feature/source-control\n";

        var e = Assert.Single(WorktreeListParser.Parse(porcelain));
        Assert.Equal("feature/source-control", e.Branch);
    }
}
