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
    Task RecordHoldAsync(string sessionId, bool onHold, CancellationToken ct = default);
}
