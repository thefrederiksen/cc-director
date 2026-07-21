using System.Collections.Generic;

namespace CcDirector.Core.GatewayConnection;

/// <summary>
/// The four ways a Director can be pointed at a Gateway at connect time (architecture two-step-install v4,
/// section 1.3). This is the choice the upgraded <c>GatewayConnectionPanel</c> offers before it scans:
/// self-host a local Gateway, use the hosted Gateway, join a Gateway that already exists, or skip and stay
/// local-only for now.
/// </summary>
public enum GatewayChoiceAction
{
    /// <summary>Provision and run a Gateway on this computer. Windows-only; not built yet (slice #1808b/#1810).</summary>
    SelfHost,

    /// <summary>Use the DevThrottle-hosted Gateway. Functional - a browser account sign-in whose token is
    /// enrolled at the hosted Gateway, which mints this machine's tenant-scoped device key.</summary>
    UseHosted,

    /// <summary>Connect to a Gateway that is already running (on the network, over Tailscale, or on the
    /// account). Functional in this slice - it drives the existing scan / manual-URL / enroll flow.</summary>
    JoinExisting,

    /// <summary>Do not connect a Gateway now. The Director runs local-only; a Gateway can be connected later
    /// from Settings.</summary>
    Skip,
}

/// <summary>
/// Which of the three consumers of the one shared <c>GatewayConnectionPanel</c> is showing the choice
/// (architecture two-step-install v4, section 0 / 1.3): first-run onboarding, the Settings Gateway tab, or
/// the main-window status box. The same state machine is FILTERED by this - it is one fork, not three
/// copies of the UI.
/// </summary>
public enum GatewayChoiceConsumer
{
    /// <summary>The first-run onboarding wizard's Gateway step.</summary>
    Onboarding,

    /// <summary>The Settings dialog's Gateway tab.</summary>
    Settings,

    /// <summary>The main-window Gateway status box's pop-out window.</summary>
    StatusWindow,
}

/// <summary>
/// Whether a choice action is offered, and if so whether it can be acted on. The dumb-client rule
/// (project coding standards, rule 7): this state machine - not the panel - decides enabled-ness; the panel
/// only renders what it is told. A disabled action is shown, clearly, as a not-yet-available "coming"
/// action; it is never a live-looking button that does nothing.
/// </summary>
public enum GatewayChoiceAvailability
{
    /// <summary>Offered and actionable.</summary>
    Enabled,

    /// <summary>Offered but not actionable yet - rendered as a clearly disabled "coming" action, never live.</summary>
    Disabled,

    /// <summary>Not offered at all on this host (for example self-host on a Mac). The panel omits it.</summary>
    Absent,
}

/// <summary>
/// What the Skip action means for the current consumer (architecture two-step-install v4, section 2,
/// #1808a). Onboarding's Skip is the local-only completion seam (issue #1809): it persists the
/// onboarding-complete marker and leaves the Director local-only. In Settings and the status box there is
/// no onboarding to complete, so Skip simply returns to the choice. (Repair reconnects a KNOWN broken
/// connection and never shows the choice at all, so it has no Skip behavior here - see the panel.)
/// </summary>
public enum GatewaySkipBehavior
{
    /// <summary>Persist the onboarding-complete marker and stay local-only (the issue #1809 seam). Onboarding only.</summary>
    CompleteOnboardingLocalOnly,

    /// <summary>Just return to the choice; complete nothing. Settings and the status box.</summary>
    ReturnToChoice,
}

/// <summary>
/// The UI-free input to <see cref="GatewayChoiceStateMachine"/> (architecture two-step-install v4, section
/// 2, #1808a). A value type with plain fields so tests construct it directly and the machine has nothing to
/// null-check. It carries exactly what the choice depends on: which consumer is showing it, and whether
/// this host can self-host a Gateway at all.
///
/// It intentionally does NOT model a repair/current-connection state. Repair reconnects a KNOWN broken
/// connection, and the panel routes that case straight to the rediscovery scan without ever showing the
/// choice (documented in the panel), so a repair dimension here would only add unreachable rows - the
/// Architect ruling for #1808a removed it. A later slice that wants choice-on-repair owns re-adding it.
/// </summary>
/// <param name="Consumer">Which of the three shared-panel consumers is showing the choice.</param>
/// <param name="SelfHostSupported">Whether this operating system can run a self-hosted Gateway. Self-host
/// is Windows-only in source, so this is false on macOS and Linux - the self-host action is then ABSENT,
/// not merely disabled.</param>
public readonly record struct GatewayChoiceContext(
    GatewayChoiceConsumer Consumer,
    bool SelfHostSupported);

/// <summary>
/// One action in a resolved choice plan: which action, whether it is enabled / disabled / absent, and - when
/// disabled - the plain-English reason the panel shows next to it.
/// </summary>
/// <param name="Action">The choice action.</param>
/// <param name="Availability">Whether the action is offered and actionable.</param>
/// <param name="DisabledReason">The reason to show when <paramref name="Availability"/> is Disabled; null otherwise.</param>
public sealed record GatewayChoiceOption(
    GatewayChoiceAction Action,
    GatewayChoiceAvailability Availability,
    string? DisabledReason);

/// <summary>
/// The complete resolved choice the panel renders (architecture two-step-install v4, section 2, #1808a):
/// the four actions with their availability, in display order, plus what Skip means for this consumer. The
/// panel reads this verbatim - it never re-derives which actions are offered or what Skip does.
/// </summary>
/// <param name="Options">The four actions in display order (self-host, hosted, join, skip). An absent action
/// is still present here with <see cref="GatewayChoiceAvailability.Absent"/>; the panel omits absent ones.</param>
/// <param name="SkipBehavior">What the Skip action does for this consumer.</param>
public sealed record GatewayChoicePlan(
    IReadOnlyList<GatewayChoiceOption> Options,
    GatewaySkipBehavior SkipBehavior)
{
    /// <summary>The availability the plan assigns to a given action (Absent if the action is not present).</summary>
    public GatewayChoiceAvailability AvailabilityOf(GatewayChoiceAction action)
    {
        foreach (var option in Options)
            if (option.Action == action)
                return option.Availability;
        return GatewayChoiceAvailability.Absent;
    }
}

/// <summary>
/// The single, pure decision point for the gateway CHOICE (architecture two-step-install v4, section 2,
/// #1808a): plain <see cref="GatewayChoiceContext"/> in, a <see cref="GatewayChoicePlan"/> out - no UI, no
/// I/O (CodingStyle Section 8: Core has no UI dependencies), mirroring
/// <see cref="GatewayConnectionStateResolver"/>.
///
/// This is the "same fork in all three consumers": ONE machine filtered by context, never three copies of
/// the UI. The load-bearing rules it enforces:
///   - Self-host is Windows-only. Where the host cannot self-host (a Mac), the action is ABSENT, not a
///     disabled tease. Where it can, it is DISABLED "coming" in this slice - the orchestrator is #1808b/#1810.
///   - Hosted is always ENABLED - a browser account sign-in enrolls this machine at the one shared hosted
///     Gateway, which mints its tenant-scoped device key (there is no address to type and nothing to provision).
///   - Join is always ENABLED - it drives the existing scan / manual-URL / enroll flow.
///   - Skip is always ENABLED; what it DOES is per-consumer (onboarding completes local-only, others return).
/// </summary>
public static class GatewayChoiceStateMachine
{
    /// <summary>The reason shown beside the not-yet-built self-host and hosted actions in this slice.</summary>
    public const string ComingSoonReason = "Coming soon";

    /// <summary>Resolve the choice for a context into the plan the panel renders.</summary>
    public static GatewayChoicePlan Resolve(GatewayChoiceContext context)
    {
        var options = new List<GatewayChoiceOption>
        {
            ResolveSelfHost(context),
            // Hosted is functional: a browser account sign-in enrolls this machine at the one shared hosted
            // Gateway, which mints its tenant-scoped device key. No address to type, nothing to provision.
            new(GatewayChoiceAction.UseHosted, GatewayChoiceAvailability.Enabled, null),
            // Join drives today's scan / manual-URL / enroll flow - always available.
            new(GatewayChoiceAction.JoinExisting, GatewayChoiceAvailability.Enabled, null),
            // Skip is always available; SkipBehavior below says what it does for this consumer.
            new(GatewayChoiceAction.Skip, GatewayChoiceAvailability.Enabled, null),
        };
        return new GatewayChoicePlan(options, ResolveSkipBehavior(context));
    }

    // Self-host is Windows-only. Absent where the OS cannot run it; disabled "coming" where it can (this
    // slice has no orchestrator - that is #1808b/#1810).
    private static GatewayChoiceOption ResolveSelfHost(GatewayChoiceContext context)
        => context.SelfHostSupported
            ? new GatewayChoiceOption(GatewayChoiceAction.SelfHost, GatewayChoiceAvailability.Disabled, ComingSoonReason)
            : new GatewayChoiceOption(GatewayChoiceAction.SelfHost, GatewayChoiceAvailability.Absent, null);

    // Only onboarding's Skip completes onboarding local-only (the issue #1809 seam). Settings/status Skip
    // just return to the choice. (Repair never reaches the choice, so it has no Skip behavior here.)
    private static GatewaySkipBehavior ResolveSkipBehavior(GatewayChoiceContext context)
        => context.Consumer == GatewayChoiceConsumer.Onboarding
            ? GatewaySkipBehavior.CompleteOnboardingLocalOnly
            : GatewaySkipBehavior.ReturnToChoice;
}
