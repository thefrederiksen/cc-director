using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Tests for the first-run wizard state machine (issue #2101): gating on the completion marker
/// (including the migration guarantee that a machine with the OLD onboarding marker never sees the
/// new wizard), the marker write, the canonical step order and mandatory bookends, back/forward
/// navigation, the progress-dot states, and the skip rules - in particular that the Agents step is
/// unskippable when zero agents are found. Config-touching tests redirect CcStorage to a temp root
/// via CC_DIRECTOR_ROOT and are serialized with the other config-env tests.
/// </summary>
[Collection("ConfigEnvSerial")]
public class FirstRunWizardModelTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-firstrun-tests", Guid.NewGuid().ToString("N"));

    private static void WithRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<WizardStep> ShellSteps() => new[]
    {
        WizardStep.Welcome, WizardStep.Agents, WizardStep.Gateway, WizardStep.Done,
    };

    // --- Gating + marker (AC1, AC2) --------------------------------------------------------------

    [Fact]
    public void ShouldShow_FreshInstall_NoMarker_ReturnsTrue()
    {
        WithRoot(() => Assert.True(FirstRunWizardModel.ShouldShow()));
    }

    [Fact]
    public void ShouldShow_AfterMarkComplete_ReturnsFalse_AndMarkerIsSet()
    {
        WithRoot(() =>
        {
            Assert.True(FirstRunWizardModel.ShouldShow());

            FirstRunWizardModel.MarkComplete();

            Assert.False(FirstRunWizardModel.ShouldShow());
            Assert.True(OnboardingModel.IsOnboardingComplete());
        });
    }

    [Fact]
    public void ShouldShow_WhenOldOnboardingMarkerPresent_ReturnsFalse_Migration()
    {
        WithRoot(() =>
        {
            // A machine that finished the OLD onboarding carries onboarding.completed. The new wizard
            // shares that marker, so such a machine must never see the new wizard.
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["onboarding"] = new JsonObject { ["completed"] = true },
            });

            Assert.False(FirstRunWizardModel.ShouldShow());
        });
    }

    [Fact]
    public void MarkComplete_DoesNotClobberOtherConfigSections()
    {
        WithRoot(() =>
        {
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["gateway"] = new JsonObject { ["url"] = "http://gateway-host:7878" },
            });

            FirstRunWizardModel.MarkComplete();

            Assert.True(OnboardingModel.IsOnboardingComplete());
            Assert.Equal("http://gateway-host:7878", GatewayConfig.Load().Url);
        });
    }

    // --- Step list / order / bookends (AC3, AC6) -------------------------------------------------

    [Fact]
    public void Constructor_NormalisesToCanonicalOrder_RegardlessOfInputOrder()
    {
        // Even handed the steps out of order, the model presents them in the one canonical order.
        var model = new FirstRunWizardModel(new[]
        {
            WizardStep.Done, WizardStep.Gateway, WizardStep.Welcome, WizardStep.Agents,
        });

        Assert.Equal(
            new[] { WizardStep.Welcome, WizardStep.Agents, WizardStep.Gateway, WizardStep.Done },
            model.Steps);
    }

    [Fact]
    public void Constructor_DeduplicatesRepeatedSteps()
    {
        var model = new FirstRunWizardModel(new[]
        {
            WizardStep.Welcome, WizardStep.Welcome, WizardStep.Done, WizardStep.Done,
        });

        Assert.Equal(new[] { WizardStep.Welcome, WizardStep.Done }, model.Steps);
    }

    [Fact]
    public void CreateFull_HasAllEightStepsInCanonicalOrder()
    {
        var model = FirstRunWizardModel.CreateFull();
        Assert.Equal(FirstRunWizardModel.CanonicalOrder, model.Steps);
        Assert.Equal(8, model.Count);

        // Tools sits right after Agents: workforce first, then their toolbelt.
        Assert.Equal(WizardStep.Tools, model.Steps[2]);
    }

    [Fact]
    public void Constructor_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FirstRunWizardModel(Array.Empty<WizardStep>()));
    }

    [Fact]
    public void Constructor_MissingWelcomeBookend_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FirstRunWizardModel(new[] { WizardStep.Agents, WizardStep.Done }));
    }

    [Fact]
    public void Constructor_MissingDoneBookend_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FirstRunWizardModel(new[] { WizardStep.Welcome, WizardStep.Agents }));
    }

    // --- Navigation (AC3) ------------------------------------------------------------------------

    [Fact]
    public void Navigation_StartsOnWelcome_MovesForwardAndBackAcrossPresentSteps()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        Assert.Equal(WizardStep.Welcome, model.Current);
        Assert.True(model.IsFirst);
        Assert.False(model.IsLast);

        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Agents, model.Current);
        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Gateway, model.Current);
        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Done, model.Current);
        Assert.True(model.IsLast);

        // Cannot move past Done.
        Assert.False(model.MoveNext());
        Assert.Equal(WizardStep.Done, model.Current);

        // Back walks the same present steps in reverse.
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Gateway, model.Current);
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Agents, model.Current);
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Welcome, model.Current);

        // Cannot move back before Welcome.
        Assert.False(model.MoveBack());
        Assert.Equal(WizardStep.Welcome, model.Current);
    }

    [Fact]
    public void GoTo_PresentStep_Moves_AbsentStep_Throws()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.GoTo(WizardStep.Gateway);
        Assert.Equal(WizardStep.Gateway, model.Current);

        // Code is not part of this present-step list.
        Assert.Throws<ArgumentException>(() => model.GoTo(WizardStep.Code));
    }

    // --- Progress dots (AC3) ---------------------------------------------------------------------

    [Fact]
    public void DotState_ReflectsPosition_PastCurrentUpcoming()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.GoTo(WizardStep.Gateway); // index 2 of 4

        Assert.Equal(WizardDotState.Past, model.DotStateAt(0));
        Assert.Equal(WizardDotState.Past, model.DotStateAt(1));
        Assert.Equal(WizardDotState.Current, model.DotStateAt(2));
        Assert.Equal(WizardDotState.Upcoming, model.DotStateAt(3));
    }

    [Fact]
    public void DotState_PositionOutOfRange_Throws()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        Assert.Throws<ArgumentOutOfRangeException>(() => model.DotStateAt(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.DotStateAt(-1));
    }

    // --- Skip rules (AC4, AC6) -------------------------------------------------------------------

    [Fact]
    public void Welcome_CarriesTheWholeWizardSkip()
    {
        Assert.True(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Welcome));
        Assert.False(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Agents));
        Assert.False(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Done));
    }

    [Fact]
    public void EveryStep_SkippableByDefault_BeforeAnAgentScan()
    {
        var model = new FirstRunWizardModel(FirstRunWizardModel.CanonicalOrder);
        foreach (var step in model.Steps)
            Assert.True(model.CanSkipStep(step));
    }

    [Fact]
    public void AgentsStep_IsUnskippable_WhenZeroAgentsFound()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.SetAgentsFound(false);
        Assert.False(model.AgentsFound);
        Assert.False(model.CanSkipStep(WizardStep.Agents));

        // Only the Agents step is blocked; every other step stays skippable.
        Assert.True(model.CanSkipStep(WizardStep.Gateway));
        Assert.True(model.CanSkipStep(WizardStep.Welcome));
    }

    [Fact]
    public void AgentsStep_BecomesSkippable_OnceAnAgentIsFound()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.SetAgentsFound(false);
        Assert.False(model.CanSkipStep(WizardStep.Agents));

        model.SetAgentsFound(true);
        Assert.True(model.CanSkipStep(WizardStep.Agents));
    }

    [Fact]
    public void CanSkipCurrent_TracksTheCurrentStepAndTheAgentsFlag()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);

        model.GoTo(WizardStep.Welcome);
        Assert.True(model.CanSkipCurrent);

        model.GoTo(WizardStep.Agents);
        Assert.False(model.CanSkipCurrent);
    }

    [Fact]
    public void DeferAgents_RecordsTheCarriedToDo_WithoutMakingTheStepSkippable()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);

        Assert.False(model.AgentsDeferred);
        model.DeferAgents();

        // The deferral is a carried to-do, not a skip: the step's skip rule is unchanged.
        Assert.True(model.AgentsDeferred);
        Assert.False(model.CanSkipStep(WizardStep.Agents));
    }

    [Fact]
    public void DeferAgents_IsCleared_WhenAnAgentIsFoundLater()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);
        model.DeferAgents();
        Assert.True(model.AgentsDeferred);

        // A later successful scan (e.g. after an install) resolves the to-do.
        model.SetAgentsFound(true);
        Assert.False(model.AgentsDeferred);
    }

    [Fact]
    public void DeferAgents_WithAgentsAlreadyFound_ReportsNoPendingToDo()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(true);

        model.DeferAgents();
        Assert.False(model.AgentsDeferred);
    }
}
