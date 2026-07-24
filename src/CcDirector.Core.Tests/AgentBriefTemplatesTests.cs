using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class AgentBriefTemplatesTests
{
    private static RepositoryStatus Repo(int uncommitted = 5, int behindMain = 0, DateTime? dirtySince = null) => new()
    {
        Path = @"D:\Repos\widget",
        Name = "widget",
        Branch = "main",
        IsClean = uncommitted == 0,
        UncommittedCount = uncommitted,
        AheadCount = 1,
        BehindCount = 2,
        BehindMainCount = behindMain,
        DirtySinceUtc = dirtySince,
        Success = true,
    };

    [Theory]
    [InlineData(AgentHandOffKind.ProtectChanges, "open a pull request")]
    [InlineData(AgentHandOffKind.CleanUp, "WAIT for approval")]
    [InlineData(AgentHandOffKind.ExplainOnly, "NO changes")]
    public void Build_EachKind_CarriesItsTask_AndTheHardRules(AgentHandOffKind kind, string marker)
    {
        var brief = AgentBriefTemplates.Build(kind, Repo());

        Assert.Contains(@"D:\Repos\widget", brief);
        Assert.Contains(marker, brief);
        Assert.Contains("NEVER force-push", brief);
        Assert.Contains("Do not add any attribution", brief);
    }

    [Fact]
    public void Build_LandOrDiscard_NamesTheWorktree()
    {
        var wt = new WorktreeInfo { Path = @"D:\Repos\widget-wt", Branch = "stranded", AheadOfMain = 3, BehindMain = 10 };
        var brief = AgentBriefTemplates.Build(AgentHandOffKind.LandOrDiscardWorktree, Repo(), wt);
        Assert.Contains(@"D:\Repos\widget-wt", brief);
        Assert.Contains("stranded", brief);
        Assert.Contains("ahead 3", brief);
    }

    [Fact]
    public void Build_NeverContainsAssistantAttribution()
    {
        // The standing law: nothing the agent produces carries assistant attribution - and the brief
        // itself must not either. Checked across every kind.
        foreach (AgentHandOffKind kind in Enum.GetValues<AgentHandOffKind>())
        {
            var brief = AgentBriefTemplates.Build(kind, Repo(), new WorktreeInfo { Path = "x", Branch = "b" });
            Assert.DoesNotContain("Claude", brief);
            Assert.DoesNotContain("Anthropic", brief);
            // The PROHIBITION may name the trailer ("no Co-authored-by trailer"); an actual trailer
            // always carries the colon - that exact shape must never appear.
            Assert.DoesNotContain("Co-Authored-By:", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Generated with [", brief);
        }
    }

    [Fact]
    public void Build_DirtySince_RendersTheSittingDays()
    {
        var brief = AgentBriefTemplates.Build(AgentHandOffKind.ProtectChanges, Repo(dirtySince: DateTime.UtcNow.AddDays(-12)));
        Assert.Contains("12 day(s)", brief);
    }
}
