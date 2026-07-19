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
            new CapabilityStatus("Claude Code", IsFound: true),
            new CapabilityStatus("Python", IsFound: true),
        ]);

        Assert.Null(notice);
    }

    [Fact]
    public void Describe_NoAgentInstalled_SaysTheBoardHasNothingToRun()
    {
        var notice = CapabilityNotice.Describe(
        [
            new CapabilityStatus("Claude Code", IsFound: false),
            new CapabilityStatus("Python", IsFound: true),
            new CapabilityStatus("Node.js", IsFound: true),
        ]);

        Assert.NotNull(notice);
        Assert.Contains("Claude Code", notice);
        Assert.Contains("nothing to run", notice);
        // Only the missing one is named - a found item must never be reported as missing.
        Assert.DoesNotContain("Node.js", notice);
    }

    [Fact]
    public void Describe_SeveralMissing_NamesEachWithItsOwnConsequence()
    {
        var notice = CapabilityNotice.Describe(
        [
            new CapabilityStatus("Claude Code", IsFound: false),
            new CapabilityStatus("Python", IsFound: false),
            new CapabilityStatus("Node.js", IsFound: false),
        ]);

        Assert.NotNull(notice);
        Assert.Contains("Claude Code", notice);
        Assert.Contains("MCP servers", notice);
        // Python's line must not imply the cc-* tools are broken - they ship their own Python.
        Assert.Contains("bring their own Python", notice);
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
        var notice = CapabilityNotice.Describe([new CapabilityStatus("Node.js", IsFound: false)]);

        Assert.NotNull(notice);
        Assert.Contains("at any time", notice);
    }
}
