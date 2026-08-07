namespace CcDirector.ControlApi;

/// <summary>
/// The Gateway is connected and REFUSED to register a session key.
///
/// This exists so the caller can tell that one state apart from every other way the fleet-tool probe
/// can fail, and it exists because it could not. <see cref="ControlApiHost.MintFleetToolProbeCredentialAsync"/>
/// has always thrown for a refusal - deliberately, so it could never be confused with the benign
/// no-Gateway state - but it threw a plain <see cref="InvalidOperationException"/>, indistinguishable
/// from a bug in the probe. The desktop's only honest response to "something went wrong, I do not
/// know what" is NO VERDICT, and no verdict renders as no row at all.
///
/// So on 2026-08-05, while every session on every machine was being refused by a Gateway that
/// predated the session key registry, the Home page - the one screen built to surface exactly this -
/// showed nothing. The failure was known, logged, and silent. Issues #2457 and #2459.
///
/// Catching this specific type is what lets the desktop say "the Gateway refused the key" instead of
/// shrugging. Anything else still means no verdict, which remains correct: an unexplained failure
/// must not be dressed up as a diagnosis.
/// </summary>
public sealed class GatewayRefusedSessionKeyException : Exception
{
    public GatewayRefusedSessionKeyException(string message) : base(message)
    {
    }
}
