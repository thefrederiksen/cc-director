using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>Why the Hub allowed or refused a broadcast (issue #1229). Drives logging and the message
/// the sender sees.</summary>
public enum BroadcastOutcome
{
    /// <summary>Every recipient is inside the sender's own team; delivered without a grant.</summary>
    AllowedInScope,

    /// <summary>Recipients reach beyond the team, but a valid human grant authorized it.</summary>
    AllowedByGrant,

    /// <summary>Recipients reach beyond the team and no valid grant was presented.</summary>
    DeniedOutOfScope,

    /// <summary>A grant was presented for an out-of-team broadcast but no reason was given.</summary>
    DeniedMissingReason,

    /// <summary>The sender could not be identified, so no scope could be established.</summary>
    DeniedUnknownSender,
}

/// <summary>The Hub's scope verdict for one broadcast (issue #1229). Pure data - the caller logs it
/// and either delivers or refuses.</summary>
public sealed record BroadcastDecision(
    bool Allowed,
    BroadcastOutcome Outcome,
    string? DeniedReason,
    IReadOnlyList<string> InScopeTargetIds,
    IReadOnlyList<string> OutOfScopeTargetIds);

/// <summary>
/// Pure, I/O-free decision for whether a fan-out may proceed on SCOPE grounds (issue #1229). Kept
/// separate from delivery and from the stateful rate-limit / grant store (<see cref="BroadcastGovernor"/>)
/// so the who-may-reach-whom rule is unit-testable in isolation, mirroring
/// <see cref="CcDirector.ControlApi.FleetMessaging"/>.
/// </summary>
public static class FleetBroadcastPolicy
{
    /// <summary>The plain-English refusal the sender sees when a broadcast leaves its team without a
    /// grant. ASCII only so it renders in every agent terminal.</summary>
    public const string OutOfScopeMessage =
        "This broadcast reaches sessions outside your own team (a different repository, machine, or group). "
        + "Message only the sessions on your own team, or ask a human to issue a broadcast grant for a "
        + "genuine fleet-wide message. See issue #1229.";

    /// <summary>
    /// Decide whether a broadcast from <paramref name="sender"/> to <paramref name="targets"/> may
    /// proceed. <paramref name="sender"/> is null when the caller could not be identified in the
    /// fleet view. <paramref name="hasValidGrant"/> is the governor's verdict on the presented grant
    /// id. <paramref name="reason"/> is the caller's justification (required only when a grant carries
    /// an out-of-team broadcast).
    /// </summary>
    /// <param name="targets">Every recipient with the scope the Hub resolved for it. Empty is allowed
    /// (nothing to deliver).</param>
    public static BroadcastDecision Evaluate(
        BroadcastScope? sender,
        IReadOnlyList<(string SessionId, BroadcastScope Scope)> targets,
        bool hasValidGrant,
        string? reason)
    {
        targets ??= Array.Empty<(string, BroadcastScope)>();

        // An unknown sender has no team, so every recipient is out of scope. A valid human grant is
        // still the override - a human vouching for the broadcast does not need the sender resolved.
        if (sender is null)
        {
            if (targets.Count == 0)
                return new BroadcastDecision(true, BroadcastOutcome.AllowedInScope, null,
                    Array.Empty<string>(), Array.Empty<string>());

            var allIds = targets.Select(t => t.SessionId).ToList();
            if (!hasValidGrant)
                return new BroadcastDecision(false, BroadcastOutcome.DeniedUnknownSender,
                    "The broadcasting session could not be identified in the fleet, so its team is unknown. "
                    + "Re-send from a live session, or use a human-issued grant. See issue #1229.",
                    Array.Empty<string>(), allIds);

            if (string.IsNullOrWhiteSpace(reason))
                return new BroadcastDecision(false, BroadcastOutcome.DeniedMissingReason,
                    "A fleet-wide broadcast needs a reason. See issue #1229.",
                    Array.Empty<string>(), allIds);

            return new BroadcastDecision(true, BroadcastOutcome.AllowedByGrant, null,
                Array.Empty<string>(), allIds);
        }

        var inScope = new List<string>();
        var outOfScope = new List<string>();
        foreach (var (sessionId, scope) in targets)
        {
            if (sender.Value.Includes(scope)) inScope.Add(sessionId);
            else outOfScope.Add(sessionId);
        }

        // Everyone is on the sender's team: the free, everyday lane (manager <-> workers, same repo).
        if (outOfScope.Count == 0)
            return new BroadcastDecision(true, BroadcastOutcome.AllowedInScope, null, inScope, outOfScope);

        // The broadcast leaves the team. Only a valid human grant lets it through, and only with a reason.
        if (!hasValidGrant)
            return new BroadcastDecision(false, BroadcastOutcome.DeniedOutOfScope, OutOfScopeMessage, inScope, outOfScope);

        if (string.IsNullOrWhiteSpace(reason))
            return new BroadcastDecision(false, BroadcastOutcome.DeniedMissingReason,
                "A broadcast beyond your team needs a reason to accompany the grant. See issue #1229.",
                inScope, outOfScope);

        return new BroadcastDecision(true, BroadcastOutcome.AllowedByGrant, null, inScope, outOfScope);
    }
}
