using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the wizard's step ordering. The installer is Director-only with no account gate (issue
/// #1807), so there is ONE linear path for every install and update: 1 Welcome, 7 Install, 8 Complete.
/// Historical ids 2-6 are retired - 6 was the Skills screen, which showed internal identifiers as
/// tick-boxes nobody could tick, and 2 was Prerequisites, which existed for the one row that could
/// block (the .NET runtime the app could not start without). The Windows executables carry their own
/// runtime now, so nothing this installer places needs anything already on the machine.
///
/// These tests pin that the visible-step list and the forward/back navigation all agree on that single
/// path and never surface the removed steps.
/// </summary>
public sealed class WizardStepFlowTests
{
    private static readonly int[] LinearPath = [1, 7, 8];

    [Fact]
    public void VisibleSteps_IsTheSingleLinearPath_WithNoPrerequisitesSignInConnectOrSkills()
    {
        var steps = WizardStepFlow.VisibleSteps();

        Assert.Equal(LinearPath, steps);
        // The retired intermediate steps never appear.
        Assert.DoesNotContain(2, steps);
        Assert.DoesNotContain(3, steps);
        Assert.DoesNotContain(4, steps);
        Assert.DoesNotContain(5, steps);
        Assert.DoesNotContain(6, steps);
    }

    [Fact]
    public void NextStep_WalksTheLinearPath_SkippingTheRemovedIds()
    {
        Assert.Equal(7, WizardStepFlow.NextStep(1));
        Assert.Equal(8, WizardStepFlow.NextStep(7));
    }

    [Fact]
    public void NextStep_FromTheLastStep_StaysPut()
    {
        // Callers guard against advancing past Complete; NextStep never runs off the end.
        Assert.Equal(8, WizardStepFlow.NextStep(8));
    }

    [Fact]
    public void PrevStep_WalksTheLinearPathBackwards_SkippingTheRemovedIds()
    {
        Assert.Equal(1, WizardStepFlow.PrevStep(7));
        Assert.Equal(7, WizardStepFlow.PrevStep(8));
    }

    [Fact]
    public void PrevStep_FromTheFirstStep_StaysPut()
    {
        Assert.Equal(1, WizardStepFlow.PrevStep(1));
    }

    [Fact]
    public void NextStep_WalkingForwardFromWelcome_VisitsExactlyTheVisibleSteps()
    {
        var visible = WizardStepFlow.VisibleSteps();

        var walked = new List<int> { visible[0] };
        var step = visible[0];
        while (step < visible[^1])
        {
            step = WizardStepFlow.NextStep(step);
            walked.Add(step);
        }

        Assert.Equal(visible, walked);
    }

    [Fact]
    public void PrevStep_WalkingBackFromComplete_VisitsExactlyTheVisibleSteps()
    {
        var visible = WizardStepFlow.VisibleSteps();

        var walked = new List<int> { visible[^1] };
        var step = visible[^1];
        while (step > visible[0])
        {
            step = WizardStepFlow.PrevStep(step);
            walked.Add(step);
        }
        walked.Reverse();

        Assert.Equal(visible, walked);
    }
}
