using CcDirector.Core.Account;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// What a surface should do when the user activates the call-to-action on a hosted-AI unavailable
/// message (issue #938). Kept semantic - NOT a raw URL - because each surface navigates its own way:
/// the desktop app opens a browser or a dialog, the Cockpit switches to its Settings AI tab, the
/// mobile app routes within itself. For <see cref="OpenBilling"/> the concrete web address is also
/// carried on <see cref="HostedAiMessage.CtaUrl"/> for surfaces that just open a browser.
/// </summary>
public enum HostedAiCtaAction
{
    /// <summary>No action (the <see cref="HostedAiState.Ready"/> message has no call-to-action).</summary>
    None = 0,

    /// <summary>Open the DevThrottle Billing page so the user can add credits or raise their limit.</summary>
    OpenBilling = 1,

    /// <summary>Open the local Settings AI tab so the user can finish DevThrottle AI setup.</summary>
    OpenSettings = 2,
}

/// <summary>
/// The single-source copy for one <see cref="HostedAiState"/> (issue #938): the sentence to show, the
/// call-to-action button label, the semantic action, and - for the billing action - the concrete web
/// address. Built only by <see cref="HostedAiMessages.For"/> so the wording lives in exactly one place
/// and is identical across every surface by construction.
/// </summary>
/// <param name="Text">The user-facing sentence. Empty for <see cref="HostedAiState.Ready"/>.</param>
/// <param name="CtaLabel">The call-to-action button label. Empty for <see cref="HostedAiState.Ready"/>.</param>
/// <param name="CtaAction">What the call-to-action does (semantic - each surface maps it to navigation).</param>
/// <param name="CtaUrl">The concrete web address for <see cref="HostedAiCtaAction.OpenBilling"/>; null otherwise.</param>
public sealed record HostedAiMessage(string Text, string CtaLabel, HostedAiCtaAction CtaAction, string? CtaUrl);

/// <summary>
/// The DevThrottle website addresses the hosted-AI call-to-actions route to (issue #938). Resolved
/// from the SAME base as the rest of the account egress (<see cref="DevThrottleApi.DefaultBaseUrl"/>
/// with the <see cref="DevThrottleApi.BaseUrlEnvVar"/> override), so a preview host is honored
/// and there is no new hard-coded domain. These are website routes (not <c>/api/v1</c> endpoints); the
/// user opens them in a browser.
/// </summary>
public static class HostedAiUrls
{
    /// <summary>The Billing page: add credits (top-up) or raise the monthly spending limit.</summary>
    public const string BillingPath = "/account/billing";

    /// <summary>The onboarding page for a brand-new account.</summary>
    public const string GetStartedPath = "/account/get-started";

    /// <summary>The website base (env override, else the production default), without a trailing slash.</summary>
    public static string WebsiteBase()
    {
        return DevThrottleApi.ResolveBaseUrl();
    }

    /// <summary>The absolute Billing URL for the add-credits / raise-limit call-to-action.</summary>
    public static string Billing => WebsiteBase() + BillingPath;

    /// <summary>The absolute onboarding URL.</summary>
    public static string GetStarted => WebsiteBase() + GetStartedPath;
}
