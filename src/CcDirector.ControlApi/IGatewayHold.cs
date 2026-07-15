using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The ONE seam through which a Director records or clears a Gateway-owned snooze/hold for a session
/// (Snooze Length mission, Phase 3). Snooze is Gateway-owned: the timer lives on the Gateway so it
/// survives a dead Director, so a desktop Snooze button must go THROUGH the Gateway rather than set
/// <c>Session.OnHold</c> in-process (the in-process set gave no timer - the Phase 3 bug we remove).
///
/// Today this is implemented as a direct Director-to-Gateway HTTP POST to
/// <c>/sessions/{sessionId}/hold</c> (<see cref="GatewayClient"/>). The Gateway Cleanup mission is
/// moving ALL Director-to-Gateway traffic onto the tunnel; its Architect (d640f023) ruled that the
/// HTTP path ships now and that snooze-hold is the first named customer for the Director-initiated
/// unary upstream verb the tunnel will add. Keeping the "how it reaches the Gateway" behind this ONE
/// interface makes that later HTTP-to-tunnel swap a one-line change of the implementation, not a hunt.
///
/// The call is fail-loud (no fallback): it THROWS when the Gateway is not configured/reachable or the
/// hold is not confirmed, so the caller can show a clear error and set NO local hold. On SUCCESS the
/// Gateway has already recorded the snooze-until AND forwarded the hold back DOWN to the owning
/// Director (which set <c>Session.OnHold</c>), so the local state is already correct when this returns.
/// </summary>
public interface IGatewayHold
{
    /// <summary>
    /// Record (<paramref name="onHold"/> = true) or clear (false) the Gateway-owned snooze for the
    /// session and forward the hold to its owning Director. Returns only after the Gateway confirms;
    /// throws on any failure (no fallback).
    /// </summary>
    /// <param name="snoozeMinutes">
    /// How long to hold it, in whole minutes. Null means "use the user's default length", which is what
    /// the plain one-click Snooze sends; a value is what a specific "Snooze for" choice sends. Ignored
    /// when <paramref name="onHold"/> is false - an unsnooze has no length.
    /// </param>
    Task RecordHoldAsync(string sessionId, bool onHold, int? snoozeMinutes = null, CancellationToken ct = default);

    /// <summary>
    /// Fetch the user's snooze lengths and default from the Gateway, or null when the Gateway is not
    /// configured. Throws when the Gateway is configured but the call fails, so a caller that needs the
    /// real answer fails loud; the desktop's cache catches that and keeps its last-known list rather than
    /// blocking a menu on the network.
    /// </summary>
    Task<SnoozeOptionsResponse?> GetSnoozeOptionsAsync(CancellationToken ct = default);
}
