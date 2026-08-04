namespace CcDirector.ControlApi;

/// <summary>
/// A short-lived Gateway credential minted for the desktop's fleet-tool health probe - the same
/// session-key shape a real session launch stamps into its environment, so the probe exercises
/// exactly the path an agent's command line uses. See
/// <see cref="ControlApiHost.MintFleetToolProbeCredentialAsync"/> for the contract.
/// </summary>
/// <param name="GatewayUrl">The Gateway base URL the probe presents the key to (what a session
/// receives as CC_GATEWAY_URL).</param>
/// <param name="SessionKey">The minted, registered key (what a session receives as
/// CC_GATEWAY_SESSION_KEY).</param>
/// <param name="Revoke">Ends the key on the Gateway. The probe MUST call this when done - the key
/// is bound to a probe-only id that never joins the session roster, so no reaper will ever end it.</param>
public sealed record FleetToolProbeCredential(string GatewayUrl, string SessionKey, Action Revoke);
