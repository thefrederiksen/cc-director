using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The pure roll-up behind the home tools row: pass/fail/not-built tally, and which not-built tools
/// count as "broken" (expected here) vs optional. Drives whether the home alarms (HasProblem).
/// </summary>
public class ToolHealthTests
{
    private static ToolHealthInput Built(string name, bool passed) => new(name, true, true, passed);
    private static ToolHealthInput NotBuilt(string name, bool expected) => new(name, false, expected, false);

    [Fact]
    public void From_TalliesPassFailNotBuiltAndBroken()
    {
        var s = ToolHealthSummary.From(new[]
        {
            Built("a", true), Built("b", true), Built("c", false),
            NotBuilt("optional", expected: false), NotBuilt("broken", expected: true),
        });

        Assert.Equal(2, s.Pass);
        Assert.Equal(1, s.Fail);
        Assert.Equal(2, s.NotBuilt);
        Assert.Equal(1, s.Broken); // only the EXPECTED not-built one is "broken"; the optional one is not
        Assert.Equal(new[] { "c" }, s.Failing);
        Assert.Equal(5, s.Total);
        Assert.True(s.HasProblem);
    }

    [Fact]
    public void From_AnyNotBuilt_IsAProblem()
    {
        // Even an optional/never-installed tool is surfaced now: the home shows the true picture and warns.
        var s = ToolHealthSummary.From(new[] { Built("a", true), Built("b", true), NotBuilt("optional", expected: false) });

        Assert.Equal(0, s.Fail);
        Assert.Equal(1, s.NotBuilt);
        Assert.True(s.HasProblem);
    }

    [Fact]
    public void From_AllBuiltAndPassing_NoProblem()
    {
        var s = ToolHealthSummary.From(new[] { Built("a", true), Built("b", true) });

        Assert.False(s.HasProblem); // green only when every tool passes
    }

    [Fact]
    public void From_BrokenButNothingFailing_IsStillAProblem()
    {
        var s = ToolHealthSummary.From(new[] { Built("a", true), NotBuilt("broken", expected: true) });

        Assert.Equal(0, s.Fail);
        Assert.Equal(1, s.Broken);
        Assert.True(s.HasProblem); // a broken (expected-but-missing) tool alarms even with no failures
    }

    // ---- Issue #1045: the failure carries its reason, and the two kinds of problem are told apart ----

    [Fact]
    public void From_FailingTool_KeepsTheReasonItGave()
    {
        // "1 fail" alone sent a reader to a log that had kept no record of why. The reason travels WITH
        // the count now, because the runner already knew it and throwing it away cost a machine rebuild.
        var s = ToolHealthSummary.From(new[]
        {
            Built("cc-html", true),
            new ToolHealthInput("cc-pdf", true, true, false, "smoke check: timed out after 90s"),
        });

        var failure = Assert.Single(s.Failures);
        Assert.Equal("cc-pdf", failure.Name);
        Assert.Equal("smoke check: timed out after 90s", failure.Reason);
        Assert.Equal("cc-pdf (smoke check: timed out after 90s)", failure.ToString());
    }

    [Fact]
    public void From_ToolPresentButFailing_IsAFaultNotSomethingToReconcile()
    {
        // The tool is installed and its own check failed. A reconcile writes shims and rebuilds venvs; it
        // has no mechanism for this, so it must not be reported as drift to retry.
        var s = ToolHealthSummary.From(new[]
        {
            Built("cc-html", true),
            new ToolHealthInput("cc-pdf", true, true, false, "smoke check: exit 1"),
        });

        Assert.True(s.HasFailingTool);
        Assert.False(s.HasMissingTool);
    }

    [Fact]
    public void From_ToolMissing_IsReconcilableDrift()
    {
        // A half-install IS a reconcile's job - it writes the shim, or rebuilds the venv behind it.
        var s = ToolHealthSummary.From(new[] { Built("cc-html", true), NotBuilt("cc-pdf", expected: true) });

        Assert.True(s.HasMissingTool);
        Assert.False(s.HasFailingTool);
    }

    [Fact]
    public void From_NoFailureReasonRecorded_StillNamesTheTool()
    {
        // A missing reason must degrade to the bare name, never to an empty row that hides the failure.
        var s = ToolHealthSummary.From(new[] { Built("cc-pdf", false) });

        var failure = Assert.Single(s.Failures);
        Assert.Equal("cc-pdf", failure.ToString());
        Assert.Equal(new[] { "cc-pdf" }, s.Failing);
    }
}
