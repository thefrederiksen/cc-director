using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests.Tools;

/// <summary>
/// The setup wizard's Tools step says one thing about nine tools, and the Director's board says another
/// about the same nine at the same moment - that was issue #1045, and these cases pin the rule that
/// makes it impossible: a screen may only assert the question it has evidence for, and no evidence
/// renders as CHECKING, never as a pass.
/// </summary>
public class ToolsScreenFoldTests
{
    private static ToolsScreenInput Tool(string name, bool available = true)
        => new(name, $"{name} does a thing", available);

    private static ToolHealthSnapshot Snapshot(params ToolCheckOutcome[] outcomes)
    {
        var inputs = outcomes.Select(o => new ToolHealthInput(
            o.Name,
            IsBuilt: o.Verdict != ToolVerdict.NotInstalled,
            IsExpected: true,
            Passed: o.Verdict == ToolVerdict.Working,
            FailureReason: o.Verdict == ToolVerdict.NotWorking ? o.Detail : null));
        return new ToolHealthSnapshot(ToolHealthSummary.From(inputs), outcomes, DateTime.UtcNow);
    }

    private static ToolsScreenRow Row(ToolsScreenView view, string name)
        => view.Rows.Single(r => r.Name == name);

    [Fact]
    public void Fold_InstalledButNotYetChecked_SaysCheckingAndClaimsNothingAboutWorking()
    {
        // The whole defect in one case: everything is on disk and nothing has been run. The old screen
        // said "Ready" and "All 9 tools are installed and up to date" from exactly this state.
        var view = ToolsScreenFold.Fold(new[] { Tool("cc-pdf"), Tool("cc-html") }, stalled: false, health: null);

        Assert.All(view.Rows, r => Assert.Equal(ToolRowVerdict.Checking, r.Verdict));
        Assert.Contains("installed", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Checking", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("up to date", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("working", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.KeepPolling); // the answer is still coming - keep looking for it
    }

    [Fact]
    public void Fold_InstalledAndFailing_NamesTheToolAndItsReason_NeverReady()
    {
        // The exact disagreement from the clean-machine run: cc-pdf present, cc-pdf failing. One surface
        // called it Ready. Here it cannot.
        var view = ToolsScreenFold.Fold(
            new[] { Tool("cc-pdf"), Tool("cc-html") },
            stalled: false,
            Snapshot(
                new ToolCheckOutcome("cc-pdf", ToolVerdict.NotWorking, "smoke check: timed out after 90s"),
                new ToolCheckOutcome("cc-html", ToolVerdict.Working, "all checks passed")));

        Assert.Equal(ToolRowVerdict.NotWorking, Row(view, "cc-pdf").Verdict);
        Assert.Contains("timed out after 90s", Row(view, "cc-pdf").Detail);
        Assert.Equal(ToolRowVerdict.Working, Row(view, "cc-html").Verdict);

        Assert.Equal(ToolsScreenTone.Bad, view.Tone);
        Assert.Contains("cc-pdf", view.StatusText);
        Assert.Contains("not working", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.OfferRepair);
    }

    [Fact]
    public void Fold_InstalledAndAllPassing_IsTheOnlyStateThatClaimsWorking()
    {
        var view = ToolsScreenFold.Fold(
            new[] { Tool("cc-pdf"), Tool("cc-html") },
            stalled: false,
            Snapshot(
                new ToolCheckOutcome("cc-pdf", ToolVerdict.Working, "all checks passed"),
                new ToolCheckOutcome("cc-html", ToolVerdict.Working, "all checks passed")));

        Assert.All(view.Rows, r => Assert.Equal(ToolRowVerdict.Working, r.Verdict));
        Assert.Equal(ToolsScreenTone.Good, view.Tone);
        Assert.Contains("working", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.OfferRepair);
        Assert.False(view.KeepPolling);
    }

    [Fact]
    public void Fold_StillInstalling_IsProgressNotFailure()
    {
        // A brand-new machine finishing its first setup is not a fault. Preserved from before #1045.
        var view = ToolsScreenFold.Fold(
            new[] { Tool("cc-pdf"), Tool("cc-html", available: false) },
            stalled: false,
            health: null);

        Assert.Equal(ToolRowVerdict.Installing, Row(view, "cc-html").Verdict);
        Assert.Equal(ToolsScreenTone.Progress, view.Tone);
        Assert.False(view.OfferRepair);
        Assert.True(view.KeepPolling);
    }

    [Fact]
    public void Fold_StalledMissingTool_StopsPromisingAndOffersRepair()
    {
        // Past the stall window, "installing" is a promise the screen cannot keep. Preserved from before.
        var view = ToolsScreenFold.Fold(
            new[] { Tool("cc-pdf"), Tool("cc-html", available: false) },
            stalled: true,
            health: null);

        Assert.Equal(ToolRowVerdict.NotInstalled, Row(view, "cc-html").Verdict);
        Assert.Contains("did not install", Row(view, "cc-html").Detail);
        Assert.Equal(ToolsScreenTone.Bad, view.Tone);
        Assert.True(view.OfferRepair);
        Assert.False(view.KeepPolling);
    }

    [Fact]
    public void Fold_SnapshotMissingATool_TreatsItAsUncheckedNotPassing()
    {
        // A snapshot that does not mention a tool says nothing about it. The fold must not read that
        // silence as a pass - the same "no evidence is not a green" rule, in its quietest form.
        var view = ToolsScreenFold.Fold(
            new[] { Tool("cc-pdf"), Tool("cc-word") },
            stalled: false,
            Snapshot(new ToolCheckOutcome("cc-pdf", ToolVerdict.Working, "all checks passed")));

        Assert.Equal(ToolRowVerdict.Checking, Row(view, "cc-word").Verdict);
    }

    [Fact]
    public void Fold_ManyFailures_SummarisesWithoutHidingTheCount()
    {
        var view = ToolsScreenFold.Fold(
            new[] { Tool("a"), Tool("b"), Tool("c") },
            stalled: false,
            Snapshot(
                new ToolCheckOutcome("a", ToolVerdict.NotWorking, "version check: exit 1"),
                new ToolCheckOutcome("b", ToolVerdict.NotWorking, "version check: exit 1"),
                new ToolCheckOutcome("c", ToolVerdict.NotWorking, "version check: exit 1")));

        Assert.Contains("3 are not working", view.StatusText);
        Assert.Contains("+1 more", view.StatusText); // truncated for width, but the count is still stated
    }
}
