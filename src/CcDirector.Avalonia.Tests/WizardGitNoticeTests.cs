using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The setup wizard's Code step tells the user when git is missing, and says nothing when it cannot
/// tell (devthrottle_internal issue #1048).
///
/// The owner's ruling is the whole specification: detect and tell, never force, never install, and
/// everything works without it. So the only thing this screen does about git is show one sentence,
/// and it shows it only for a DEFINITE absence.
/// </summary>
public class WizardGitNoticeTests
{
    private static FirstRunWizardDialog Wizard() => new(new AgentOptions());

    private static GitPresence With(GitAvailability availability)
        => new(availability, availability == GitAvailability.Present ? "C:\\Program Files\\Git\\cmd\\git.exe" : null,
            availability == GitAvailability.Present ? "git version 2.45.1" : null, "test");

    [AvaloniaFact]
    public void NoticeIsHiddenBeforeAnythingHasBeenDetected()
    {
        var wizard = Wizard();

        // A screen that accuses the machine before it has looked is worse than one that never does.
        Assert.False(wizard.CodeNoGitPanel.IsVisible);
        Assert.Null(wizard.DetectedGitPresence);
    }

    [AvaloniaFact]
    public void GitMissing_ShowsTheRecommendation()
    {
        var wizard = Wizard();

        wizard.ApplyGitPresence(With(GitAvailability.NotFound));

        Assert.True(wizard.CodeNoGitPanel.IsVisible);
    }

    [AvaloniaFact]
    public void GitPresent_SaysNothing()
    {
        var wizard = Wizard();

        wizard.ApplyGitPresence(With(GitAvailability.Present));

        Assert.False(wizard.CodeNoGitPanel.IsVisible);
    }

    /// <summary>
    /// The state the whole three-way split exists for. A probe that could not reach a verdict must
    /// leave the screen silent - telling someone with git installed that they have not got it is the
    /// failure this avoids, and it is the one a two-state detector cannot avoid.
    /// </summary>
    [AvaloniaFact]
    public void GitUndetermined_SaysNothing()
    {
        var wizard = Wizard();

        wizard.ApplyGitPresence(With(GitAvailability.Undetermined));

        Assert.False(wizard.CodeNoGitPanel.IsVisible);
    }

    /// <summary>
    /// THE WIRING. Every other test here drives ApplyGitPresence directly, so deleting the detection
    /// call from ShowStep would leave all of them green - the reviewer's point, and it was right.
    /// This one proves that arriving at the Code step is what starts the probe.
    /// </summary>
    [AvaloniaFact]
    public void ArrivingAtTheCodeStep_StartsTheGitProbe()
    {
        var wizard = Wizard();
        Assert.False(wizard.GitProbeStarted);

        wizard.ShowStepForTests(WizardStep.Code);

        Assert.True(wizard.GitProbeStarted, "reaching the Code step did not start git detection");
    }

    /// <summary>
    /// Part two of issue #1048: the clean-machine acknowledgement must hold on a machine that cannot
    /// clone. "Clone your first repository" was advice the reader could not act on; making a folder
    /// always works, and is exactly what the clean-machine walk did.
    /// </summary>
    [AvaloniaFact]
    public void TheCleanMachineAcknowledgementDoesNotRequireGit()
    {
        var wizard = Wizard();

        var text = wizard.CodeNoneAckText.Text ?? "";

        Assert.Contains("Make a folder", text);
        Assert.DoesNotContain("When you clone", text);
    }
}
