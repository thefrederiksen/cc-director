using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// The UI-free acceptance matrix for the gateway CHOICE (architecture two-step-install v4, section 2,
/// #1808a). One state machine, filtered by context, drives all three shared-panel consumers - so this pins
/// the EXACT allowed-action set for every context: onboarding / Settings / status x Windows / Mac x fresh /
/// repair. Self-host is Windows-only, so it is ABSENT on a Mac and a disabled "coming" action on Windows;
/// Hosted is a disabled "coming" action everywhere; Join is always enabled; Skip is always enabled but
/// what it DOES is per-consumer. These are the load-bearing rules the panel renders verbatim (dumb-client
/// rule); if any regresses, the matrix theory below goes red.
/// </summary>
public class GatewayChoiceStateMachineTests
{
    // The full 3 x 2 x 2 acceptance matrix. selfHostSupported models the host OS (Windows true, Mac false);
    // isRepair models whether the panel opened to repair a broken connection. The expected columns are the
    // exact availability of each of the four actions plus the resolved Skip behavior.
    [Theory]
    // Onboarding, Windows (self-host disabled/coming), fresh vs repair.
    [InlineData(GatewayChoiceConsumer.Onboarding, true, false, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Onboarding, true, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    // Onboarding, Mac (self-host ABSENT), fresh vs repair.
    [InlineData(GatewayChoiceConsumer.Onboarding, false, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Onboarding, false, true, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    // Settings, Windows / Mac, fresh vs repair - Skip always returns to choice (no onboarding to complete).
    [InlineData(GatewayChoiceConsumer.Settings, true, false, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.Settings, true, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.Settings, false, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.Settings, false, true, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    // Status window, Windows / Mac, fresh vs repair - same as Settings.
    [InlineData(GatewayChoiceConsumer.StatusWindow, true, false, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, true, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, false, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, false, true, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    public void Resolve_ForEveryContext_YieldsTheExactAllowedActionSet(
        GatewayChoiceConsumer consumer,
        bool selfHostSupported,
        bool isRepair,
        GatewayChoiceAvailability expectedSelfHost,
        GatewaySkipBehavior expectedSkip)
    {
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(consumer, selfHostSupported, isRepair));

        // Self-host: disabled where the OS supports it, absent where it does not.
        Assert.Equal(expectedSelfHost, plan.AvailabilityOf(GatewayChoiceAction.SelfHost));
        // Hosted is a disabled "coming" action everywhere in this slice.
        Assert.Equal(GatewayChoiceAvailability.Disabled, plan.AvailabilityOf(GatewayChoiceAction.UseHosted));
        // Join and Skip are always enabled.
        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.JoinExisting));
        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.Skip));
        // What Skip DOES is per-consumer / per-repair.
        Assert.Equal(expectedSkip, plan.SkipBehavior);
    }

    [Fact]
    public void SelfHost_IsAbsentOnAHostThatCannotSelfHost()
    {
        // A Mac cannot self-host a Gateway, so the action is ABSENT (omitted), never a disabled tease.
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: false, IsRepair: false));

        Assert.Equal(GatewayChoiceAvailability.Absent, plan.AvailabilityOf(GatewayChoiceAction.SelfHost));
        Assert.DoesNotContain(plan.Options, o => o.Action == GatewayChoiceAction.SelfHost
            && o.Availability != GatewayChoiceAvailability.Absent);
    }

    [Fact]
    public void SelfHostAndHosted_AreDisabledComingActions_NeverEnabled()
    {
        // On a host that can self-host, both not-yet-built provisioning actions are shown DISABLED with a
        // reason - never live buttons that would no-op (dumb-client rule).
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(GatewayChoiceConsumer.Settings, SelfHostSupported: true, IsRepair: false));

        var selfHost = Assert.Single(plan.Options, o => o.Action == GatewayChoiceAction.SelfHost);
        Assert.Equal(GatewayChoiceAvailability.Disabled, selfHost.Availability);
        Assert.Equal(GatewayChoiceStateMachine.ComingSoonReason, selfHost.DisabledReason);

        var hosted = Assert.Single(plan.Options, o => o.Action == GatewayChoiceAction.UseHosted);
        Assert.Equal(GatewayChoiceAvailability.Disabled, hosted.Availability);
        Assert.Equal(GatewayChoiceStateMachine.ComingSoonReason, hosted.DisabledReason);

        // Neither is ever offered as an enabled/actionable action in this slice.
        Assert.NotEqual(GatewayChoiceAvailability.Enabled, selfHost.Availability);
        Assert.NotEqual(GatewayChoiceAvailability.Enabled, hosted.Availability);
    }

    [Theory]
    [InlineData(GatewayChoiceConsumer.Onboarding)]
    [InlineData(GatewayChoiceConsumer.Settings)]
    [InlineData(GatewayChoiceConsumer.StatusWindow)]
    public void JoinExisting_IsEnabledForEveryConsumer(GatewayChoiceConsumer consumer)
    {
        // Join drives today's scan / manual-URL / enroll flow, so it is functional (enabled) everywhere -
        // this is what keeps all three consumers able to connect via LAN scan, manual URL, and join.
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(consumer, SelfHostSupported: true, IsRepair: false));

        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.JoinExisting));
    }

    [Fact]
    public void OnboardingFreshSkip_CompletesLocalOnly_ButRepairAndOtherConsumersReturnToChoice()
    {
        // Only a first-time onboarding Skip is the issue #1809 local-only completion seam. A repair-mode
        // Skip (onboarding is long done) and any Settings/status Skip just return to the choice.
        Assert.Equal(GatewaySkipBehavior.CompleteOnboardingLocalOnly,
            GatewayChoiceStateMachine.Resolve(
                new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, true, IsRepair: false)).SkipBehavior);

        Assert.Equal(GatewaySkipBehavior.ReturnToChoice,
            GatewayChoiceStateMachine.Resolve(
                new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, true, IsRepair: true)).SkipBehavior);

        Assert.Equal(GatewaySkipBehavior.ReturnToChoice,
            GatewayChoiceStateMachine.Resolve(
                new GatewayChoiceContext(GatewayChoiceConsumer.Settings, true, IsRepair: false)).SkipBehavior);
    }
}
