namespace CcDirector.Core.GatewayConnection;

/// <summary>
/// Whether hosted inference is ready to use once a Gateway is connected and signed in (architecture
/// two-step-install v4, sections 1.4 / 2). A connected, healthy Gateway does NOT imply inference is
/// usable: the account-minted <c>dt_live_</c> inference key is auto-provisioned best-effort, with no retry
/// or readiness wait yet, so Gateway health must never be read as inference readiness. In slice #1808a this
/// is a placeholder that reports <see cref="NotReady"/>; the real readiness contract is #1810.
/// </summary>
public enum GatewayInferenceReadiness
{
    /// <summary>Readiness has not been determined.</summary>
    Unknown,

    /// <summary>Inference is not confirmed ready. The honest default until #1810 supplies a real check.</summary>
    NotReady,

    /// <summary>Inference is confirmed ready (never reported by this slice; reserved for #1810).</summary>
    Ready,
}

/// <summary>
/// The one common terminal result the <c>GatewayConnectionPanel</c> raises when it settles (architecture
/// two-step-install v4, section 2, #1808a). Today the panel's <c>ConnectionVerified</c> event fires on the
/// TRANSPORT handshake alone, so a consumer could advance on a connection that is not yet signed in or
/// inference-ready. This result carries the full picture - connected, signed in, and inference readiness -
/// so a consumer (the onboarding wizard) advances on the whole outcome, not on transport alone.
/// </summary>
/// <param name="Connected">Whether the two-way handshake is proven.</param>
/// <param name="SignedIn">Whether the Gateway reports a signed-in account for this device.</param>
/// <param name="Inference">Whether hosted inference is ready (a NotReady placeholder in this slice; #1810).</param>
public sealed record GatewayConnectionOutcome(
    bool Connected,
    bool SignedIn,
    GatewayInferenceReadiness Inference)
{
    /// <summary>The terminal result for a panel that reached the Done view: connected AND signed in, with the
    /// given inference readiness (a NotReady placeholder in this slice until #1810).</summary>
    public static GatewayConnectionOutcome ConnectedAndSignedIn(GatewayInferenceReadiness inference)
        => new(Connected: true, SignedIn: true, Inference: inference);
}
