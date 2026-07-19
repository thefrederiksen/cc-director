using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Complete screen's honesty guard. Recommended prerequisites no longer block the wizard, so
/// this notice is the ONLY place a user is told what they skipped and what it costs them.
/// </summary>
public class CapabilityNoticeTests
{
    [Fact]
    public void Describe_NothingMissing_SaysNothing()
    {
        var notice = CapabilityNotice.Describe(
        [
            new CapabilityStatus(PrerequisiteNames.ClaudeCode, IsFound: true),
            new CapabilityStatus(PrerequisiteNames.Python, IsFound: true),
        ]);

        Assert.Null(notice);
    }

    [Fact]
    public void Describe_NoAgentAtAll_SaysTheBoardHasNothingToRun()
    {
        var notice = CapabilityNotice.Describe(
        [
            new CapabilityStatus(PrerequisiteNames.ClaudeCode, IsFound: false),
            new CapabilityStatus(PrerequisiteNames.NodeJs, IsFound: true),
        ], anotherAgentPresent: false);

        Assert.NotNull(notice);
        Assert.Contains("nothing to run", notice);
        // Only the missing one is named - a found item must never be reported as missing.
        Assert.DoesNotContain(PrerequisiteNames.NodeJs, notice);
    }

    [Fact]
    public void Describe_UserRunsAnotherAgent_DoesNotClaimTheyHaveNothingToRun()
    {
        // The whole point of the re-classification is that seven other agents exist. Telling a
        // Codex or Gemini user their board has nothing to run would repeat, in words, the mistake
        // the classification change removed.
        var notice = CapabilityNotice.Describe(
            [new CapabilityStatus(PrerequisiteNames.ClaudeCode, IsFound: false)],
            anotherAgentPresent: true);

        Assert.NotNull(notice);
        Assert.DoesNotContain("nothing to run", notice);
        Assert.Contains("other coding agent still works", notice);
    }

    [Fact]
    public void Describe_NeverClaimsSomethingIsNotInstalled()
    {
        // IsFound is false for a Python 3.9 that is very much installed - the checker reports
        // "Too old (need 3.11+)". Asserting "Not installed" would contradict the row the user
        // just read on the previous screen.
        var notice = CapabilityNotice.Describe([new CapabilityStatus(PrerequisiteNames.Python, IsFound: false)]);

        Assert.NotNull(notice);
        Assert.DoesNotContain("Not installed", notice);
        Assert.Contains("Missing or out of date", notice);
    }

    [Fact]
    public void Describe_SeveralMissing_NamesEachWithItsOwnConsequence()
    {
        var notice = CapabilityNotice.Describe(
        [
            new CapabilityStatus(PrerequisiteNames.ClaudeCode, IsFound: false),
            new CapabilityStatus(PrerequisiteNames.Python, IsFound: false),
            new CapabilityStatus(PrerequisiteNames.NodeJs, IsFound: false),
        ]);

        Assert.NotNull(notice);
        Assert.Contains("MCP servers", notice);
        // Python's line must not imply the cc-* tools are broken - they ship their own Python.
        Assert.Contains("bring their own Python", notice);
    }

    [Fact]
    public void Describe_EveryRecommendedName_HasItsOwnConsequence()
    {
        // Guards the magic-string linkage: rename a row and this fails rather than silently
        // degrading every user to the generic "some features are unavailable".
        foreach (var name in PrerequisiteNames.Recommended)
        {
            var notice = CapabilityNotice.Describe([new CapabilityStatus(name, IsFound: false)]);
            Assert.NotNull(notice);
            Assert.DoesNotContain("some features are unavailable", notice);
        }
    }

    [Fact]
    public void Describe_UnknownItem_StillReportsItRatherThanDroppingIt()
    {
        var notice = CapabilityNotice.Describe([new CapabilityStatus("Something New", IsFound: false)]);

        Assert.NotNull(notice);
        Assert.Contains("Something New", notice);
    }

    [Fact]
    public void Describe_AlwaysSaysTheGapIsRecoverable()
    {
        var notice = CapabilityNotice.Describe([new CapabilityStatus(PrerequisiteNames.NodeJs, IsFound: false)]);

        Assert.NotNull(notice);
        Assert.Contains("at any time", notice);
    }

    [Fact]
    public void AnyOtherAgent_RecognisesTheNonClaudeAgents()
    {
        Assert.True(AgentPresence.AnyOtherAgent(exe => exe == "codex"));
        Assert.True(AgentPresence.AnyOtherAgent(exe => exe == "gemini"));
        Assert.False(AgentPresence.AnyOtherAgent(_ => false));
        // Claude itself is not an "other" agent - it is the row being reported on.
        Assert.False(AgentPresence.AnyOtherAgent(exe => exe == "claude"));
    }
}
