using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class GitSyncStatusProviderTests
{
    [Fact]
    public void ParseBranchHeaders_NormalBranchWithUpstream()
    {
        var output = """
            # branch.oid abc123
            # branch.head feature-x
            # branch.upstream origin/feature-x
            # branch.ab +3 -1
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal("feature-x", result.BranchName);
        Assert.False(result.IsDetachedHead);
        Assert.True(result.HasUpstream);
        Assert.Equal(3, result.AheadCount);
        Assert.Equal(1, result.BehindCount);
    }

    [Fact]
    public void ParseBranchHeaders_NoUpstream()
    {
        var output = """
            # branch.oid abc123
            # branch.head feature-x
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal("feature-x", result.BranchName);
        Assert.False(result.HasUpstream);
        Assert.Equal(0, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
    }

    [Fact]
    public void ParseBranchHeaders_DetachedHead()
    {
        var output = """
            # branch.oid abc123
            # branch.head (detached)
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal("(detached)", result.BranchName);
        Assert.True(result.IsDetachedHead);
        Assert.False(result.HasUpstream);
    }

    [Fact]
    public void ParseBranchHeaders_OnMainBranch()
    {
        var output = """
            # branch.oid abc123
            # branch.head main
            # branch.upstream origin/main
            # branch.ab +0 -0
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal("main", result.BranchName);
        Assert.Equal(-1, result.BehindMainCount);
        Assert.Equal(0, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
    }

    [Fact]
    public void ParseBranchHeaders_OnMasterBranch()
    {
        var output = """
            # branch.oid abc123
            # branch.head master
            # branch.upstream origin/master
            # branch.ab +0 -0
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal("master", result.BranchName);
        Assert.Equal(-1, result.BehindMainCount);
    }

    [Fact]
    public void ParseBranchHeaders_ZeroCounts()
    {
        var output = """
            # branch.oid abc123
            # branch.head feature-y
            # branch.upstream origin/feature-y
            # branch.ab +0 -0
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.True(result.Success);
        Assert.Equal(0, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
        Assert.True(result.HasUpstream);
    }

    [Fact]
    public void ParseBranchHeaders_AheadOnly()
    {
        var output = """
            # branch.oid abc123
            # branch.head feature-z
            # branch.upstream origin/feature-z
            # branch.ab +5 -0
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.Equal(5, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
    }

    [Fact]
    public void ParseBranchHeaders_BehindOnly()
    {
        var output = """
            # branch.oid abc123
            # branch.head feature-w
            # branch.upstream origin/feature-w
            # branch.ab +0 -7
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.Equal(0, result.AheadCount);
        Assert.Equal(7, result.BehindCount);
    }

    [Fact]
    public void ParseBranchHeaders_EmptyOutput()
    {
        var result = GitSyncStatusProvider.ParseBranchHeaders("");

        Assert.True(result.Success);
        Assert.Equal("", result.BranchName);
        Assert.False(result.IsDetachedHead);
        Assert.False(result.HasUpstream);
    }

    [Fact]
    public void ParseBranchHeaders_WithFileStatusLines()
    {
        // Real output includes file change lines after branch headers
        var output = """
            # branch.oid abc123
            # branch.head feature-x
            # branch.upstream origin/feature-x
            # branch.ab +1 -2
            1 .M N... 100644 100644 100644 abc def src/file.cs
            ? untracked.txt
            """;

        var result = GitSyncStatusProvider.ParseBranchHeaders(output);

        Assert.Equal("feature-x", result.BranchName);
        Assert.Equal(1, result.AheadCount);
        Assert.Equal(2, result.BehindCount);
    }
}
