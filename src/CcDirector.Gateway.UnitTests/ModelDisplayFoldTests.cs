using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one place "which model is this session running" is ruled (issue devthrottle_internal#1340). Four
/// surfaces render this fold - the desktop rail, the desktop session header, the Cockpit Fleet Map and
/// roster, and the command line table - and the point of the fold is that none of them decides anything.
///
/// The assertion that matters most is that the TWO ABSENCES stay distinguishable. They mean opposite
/// things: one says a model is coming, the other says one is never coming. Rendered the same, they send a
/// reader hunting for a setting that does not exist, or waiting for a value that will never arrive.
/// </summary>
public sealed class ModelDisplayFoldTests
{
    private static readonly string[] CanReport = ["Cancel", "ModelReport", "TokenUsage"];
    private static readonly string[] CannotReport = ["Cancel", "Interrupt"];

    [Fact]
    public void RecordedModel_IsReported_WithFullIdInTheTooltip()
    {
        var d = ModelDisplayFold.For("claude-fable-5", CanReport);
        Assert.Equal("reported", d.Kind);
        Assert.Equal("fable-5", d.Text);
        Assert.Equal("claude-fable-5", d.ModelId);
        // The badge is shortened; the tooltip must still carry the id the records actually spell, or the
        // shortening becomes a claim about a model name nobody wrote down.
        Assert.Equal("claude-fable-5", d.Tooltip);
        Assert.False(d.IsAbsent);
    }

    [Fact]
    public void NonClaudeModel_IsUsedExactlyAsRecorded()
    {
        // Only the "claude-" prefix is dropped, and only because the badge it rides on already says
        // "Claude Code". Every other id is the records' own spelling, untouched.
        var d = ModelDisplayFold.For("gpt-5.6-sol", CanReport);
        Assert.Equal("gpt-5.6-sol", d.Text);
        Assert.Equal("gpt-5.6-sol", d.ModelId);
    }

    [Fact]
    public void CanReportButNothingRecorded_SaysTheModelHasNotArrivedYet()
    {
        var d = ModelDisplayFold.For(null, CanReport);
        Assert.Equal("notRecordedYet", d.Kind);
        Assert.Equal("no model yet", d.Text);
        Assert.Null(d.ModelId);
        Assert.True(d.IsAbsent);
        // The tooltip states the FACT and the MECHANISM. It must NOT name a cause: a null model is as
        // consistent with a read that could not be taken as with a first turn that has not landed, and
        // picking one reads as diagnosis (caught in review of pull request 2449).
        Assert.Contains("No model recorded yet", d.Tooltip);
        Assert.Contains("turn-end", d.Tooltip);
        Assert.DoesNotContain("has not completed a turn", d.Tooltip);
    }

    [Fact]
    public void CannotReport_SaysTheAgentWillNeverReportOne()
    {
        var d = ModelDisplayFold.For(null, CannotReport);
        Assert.Equal("notReported", d.Kind);
        Assert.Equal("model not reported", d.Text);
        Assert.Null(d.ModelId);
        Assert.True(d.IsAbsent);
        Assert.Contains("does not report", d.Tooltip);
    }

    [Fact]
    public void TheTwoAbsencesNeverRenderTheSame()
    {
        // The whole issue in one assertion. Both are "no model", and a surface that showed one string for
        // both would tell a Gemini user to keep waiting for a value that is never coming.
        var notYet = ModelDisplayFold.For(null, CanReport);
        var never = ModelDisplayFold.For(null, CannotReport);
        Assert.NotEqual(notYet.Kind, never.Kind);
        Assert.NotEqual(notYet.Text, never.Text);
        Assert.NotEqual(notYet.Tooltip, never.Tooltip);
    }

    [Fact]
    public void NoCapabilitiesAtAll_ReadsAsCannotReport()
    {
        // A Director that predates the driver layer reports no capabilities. Nothing on it will ever
        // produce a model, so "cannot report" is the truthful answer - not a hopeful "any moment now".
        Assert.Equal("notReported", ModelDisplayFold.For(null, null).Kind);
        Assert.Equal("notReported", ModelDisplayFold.For(null, []).Kind);
    }

    [Fact]
    public void BlankModel_IsTreatedAsNoModel_NotAsAModelCalledNothing()
    {
        Assert.True(ModelDisplayFold.For("   ", CanReport).IsAbsent);
        Assert.True(ModelDisplayFold.For("", CanReport).IsAbsent);
    }

    [Fact]
    public void CapabilityMatchIsCaseInsensitive()
    {
        Assert.Equal("notRecordedYet", ModelDisplayFold.For(null, ["modelreport"]).Kind);
    }

    [Fact]
    public void OverlongModelId_IsTruncatedForTheBadgeAndWholeInTheTooltip()
    {
        var id = "some-vendor-model-with-a-very-long-name-v3";
        var d = ModelDisplayFold.For(id, CanReport);
        Assert.True(d.Text.Length <= 22, $"badge text was {d.Text.Length} characters: {d.Text}");
        Assert.EndsWith("...", d.Text);
        Assert.Equal(id, d.ModelId);
        Assert.Equal(id, d.Tooltip);
    }

    [Fact]
    public void ClaudePrefixAloneIsNeverStrippedToNothing()
    {
        // Defensive: an id that IS the prefix would otherwise shorten to an empty badge, which is the one
        // thing this fold promises never to render.
        Assert.Equal("claude-", ModelDisplayFold.ShortenForBadge("claude-"));
    }

    [Fact]
    public void FoldingASessionReadsTheSessionsOwnTwoFields()
    {
        var s = new SessionDto { CurrentModel = "claude-opus-5" };
        s.DriverCapabilities.Add("ModelReport");
        var d = ModelDisplayFold.For(s);
        Assert.Equal("opus-5", d.Text);
        Assert.Equal("claude-opus-5", d.ModelId);
    }
}
