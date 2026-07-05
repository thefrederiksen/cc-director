namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The wire form of one hosted-AI unavailable message (issue #939, epic #937): the shared
/// single-source copy carried to clients so every surface (web, mobile, native) renders the identical
/// "you need credit / you need a key" state and its call-to-action. Built by the Gateway from the
/// Core <c>HostedAiMessages</c> / <c>HostedAiState</c> - this DTO is a plain string carrier so the
/// Contracts assembly stays free of a Core dependency.
/// </summary>
public sealed class HostedAiMessageDto
{
    /// <summary>The readiness state name: "NeedsCredits", "CapReached", or "NeedsKey".</summary>
    public string State { get; set; } = "";

    /// <summary>The user-facing sentence to show.</summary>
    public string Text { get; set; } = "";

    /// <summary>The call-to-action button label.</summary>
    public string CtaLabel { get; set; } = "";

    /// <summary>The semantic call-to-action: "OpenBilling" or "OpenSettings".</summary>
    public string CtaAction { get; set; } = "";

    /// <summary>The concrete web address for the billing call-to-action; null for a surface-local action.</summary>
    public string? CtaUrl { get; set; }
}
