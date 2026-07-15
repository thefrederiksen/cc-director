namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Defect 5: the payload of the <c>set-resolved-role</c> command - the Gateway telling a Director what one
/// of its sessions' resolved role IS, so the Director's desktop can fold the same answer the phone and the
/// Cockpit fold.
///
/// This is a FACT being delivered, not a request for the Director to decide anything. The Director stores
/// it verbatim on <c>Session.GatewayResolvedRole</c> and reports it back out through
/// <c>ControlEndpoints.Map</c>; it never computes, adjusts, or second-guesses the value. "Is this session's
/// controller still alive?" is unanswerable from one Director - which is why the answer has to arrive from
/// here. See docs/new_architecture/session-state.html, defect 5.
/// </summary>
public sealed class SetResolvedRoleRequest
{
    /// <summary>
    /// The resolved role: one of the <see cref="SessionRoles"/> values (Standalone / Manager / Worker /
    /// Architect). An empty or whitespace value CLEARS the stamp back to "no answer" (the Director then
    /// reports null, exactly as it does before any Gateway has ever spoken to it).
    /// </summary>
    public string Role { get; set; } = "";
}
