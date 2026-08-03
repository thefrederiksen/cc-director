using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The setup wizard's clean-machine acknowledgement must hold on a machine that cannot clone
/// (devthrottle_internal issue #1048).
///
/// "I don't have code on this machine yet" is shown to exactly the person least likely to have git:
/// someone on a clean Windows box that has never been set up for development. Telling them to clone
/// a repository is advice they cannot act on, and the failure is quiet and late - they finish
/// onboarding, go looking for the code they were told to clone, and meet "git: command not found"
/// outside the product with nothing connecting it back to anything DevThrottle said.
///
/// Making a folder always works and needs nothing installed.
/// </summary>
public class WizardCleanMachineWordingTests
{
    [AvaloniaFact]
    public void TheAcknowledgementDoesNotRequireGit()
    {
        var wizard = new FirstRunWizardDialog(new AgentOptions());

        var text = wizard.CodeNoneAckText.Text ?? "";

        Assert.Contains("Make a folder", text);
        Assert.DoesNotContain("When you clone", text);
    }

    /// <summary>
    /// Cloning is still offered - the point is that it is no longer the ONLY route. A rewrite that
    /// removed it entirely would be its own kind of wrong: plenty of readers do have git.
    /// </summary>
    [AvaloniaFact]
    public void BringingARepositoryOntoTheMachineIsStillOffered()
    {
        var wizard = new FirstRunWizardDialog(new AgentOptions());

        Assert.Contains("bring a repository onto this machine", wizard.CodeNoneAckText.Text ?? "");
    }
}
