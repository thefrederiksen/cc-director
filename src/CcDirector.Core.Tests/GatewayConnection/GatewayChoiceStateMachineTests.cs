using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// The UI-free acceptance matrix for the gateway CHOICE (architecture two-step-install v4, section 2,
/// #1808a). One state machine, filtered by context, drives all three shared-panel consumers - so this pins
/// the EXACT allowed-action set for every REACHABLE context: onboarding / Settings / status x Windows /
/// Mac. Self-host is Windows-only, so it is ABSENT on a Mac and a disabled "coming" action on Windows;
/// Hosted is a disabled "coming" action everywhere; Join is always enabled; Skip is always enabled but what
/// it DOES is per-consumer.
///
/// There is no repair dimension: the Architect ruling for #1808a removed <c>IsRepair</c> because repair
/// routes straight to the rediscovery scan and never shows the choice, so a repair row would be false
/// coverage of an unreachable state. This is the Core value guard only; the RENDERED panel behavior
/// (card actionability, Mac omission, Skip routing, terminal emission) is proved by the Avalonia
/// integration tests, not by reading these enum values.
/// </summary>
public class GatewayChoiceStateMachineTests
{
    // The full 3 x 2 acceptance matrix. selfHostSupported models the host OS (Windows true, Mac false). The
    // expected columns are the exact availability of self-host plus the resolved Skip behavior; Hosted /
    // Join / Skip availability is asserted constant inside the body.
    [Theory]
    [InlineData(GatewayChoiceConsumer.Onboarding, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Onboarding, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Settings, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.Settings, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, true, GatewayChoiceAvailability.Disabled, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, false, GatewayChoiceAvailability.Absent, GatewaySkipBehavior.ReturnToChoice)]
    public void Resolve_ForEveryContext_YieldsTheExactAllowedActionSet(
        GatewayChoiceConsumer consumer,
        bool selfHostSupported,
        GatewayChoiceAvailability expectedSelfHost,
        GatewaySkipBehavior expectedSkip)
    {
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(consumer, selfHostSupported));

        // Self-host: disabled where the OS supports it, absent where it does not.
        Assert.Equal(expectedSelfHost, plan.AvailabilityOf(GatewayChoiceAction.SelfHost));
        // Hosted is now functional - a browser account sign-in enrolls this machine at the hosted Gateway.
        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.UseHosted));
        // Join and Skip are always enabled.
        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.JoinExisting));
        Assert.Equal(GatewayChoiceAvailability.Enabled, plan.AvailabilityOf(GatewayChoiceAction.Skip));
        // What Skip DOES is per-consumer.
        Assert.Equal(expectedSkip, plan.SkipBehavior);
    }

    [Fact]
    public void SelfHost_IsADisabledComingAction_WithAReason_NeverEnabled()
    {
        // On a host that can self-host, the not-yet-built provisioning action is shown DISABLED with a
        // reason - never a live button that would no-op (dumb-client rule). The reason pin is the value this
        // adds beyond the matrix availability.
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(GatewayChoiceConsumer.Settings, SelfHostSupported: true));

        var selfHost = Assert.Single(plan.Options, o => o.Action == GatewayChoiceAction.SelfHost);
        Assert.Equal(GatewayChoiceAvailability.Disabled, selfHost.Availability);
        Assert.Equal(GatewayChoiceStateMachine.ComingSoonReason, selfHost.DisabledReason);
        Assert.NotEqual(GatewayChoiceAvailability.Enabled, selfHost.Availability);
    }

    [Fact]
    public void Hosted_IsEnabled_WithNoDisabledReason()
    {
        // Hosted is functional now: a browser account sign-in enrolls this machine at the one shared hosted
        // Gateway, which mints its tenant-scoped device key. It is offered ENABLED (no "coming" reason).
        var plan = GatewayChoiceStateMachine.Resolve(
            new GatewayChoiceContext(GatewayChoiceConsumer.Settings, SelfHostSupported: true));

        var hosted = Assert.Single(plan.Options, o => o.Action == GatewayChoiceAction.UseHosted);
        Assert.Equal(GatewayChoiceAvailability.Enabled, hosted.Availability);
        Assert.Null(hosted.DisabledReason);
    }
}
