namespace CcDirector.Gateway;

/// <summary>
/// The IMMUTABLE hosted-image identity marker. It is an assembly-level attribute that the hosted container
/// executable (<c>CcDirector.Gateway.Host</c>) stamps onto ITSELF at compile time, and that the local dev
/// console host (<c>CcDirector.Gateway</c>) and the Windows tray skin do NOT. So "am I the hosted build" is
/// answered by the compiled ARTIFACT, not by a single runtime toggle that a slot swap, a config restore, or
/// a lost environment variable can drop.
///
/// This is the fix for the fail-OPEN hole (production-readiness item MH-3 / TOP-ISSUES #8): before it,
/// hosted identity was ONLY <c>CC_GATEWAY_HOSTED=1</c>, so a hosted deployment that lost that one variable
/// booted the SAME public image as a Local single-tenant Gateway - the async tenant boundary disappeared,
/// live-money entitlement enforcement relaxed, and every hosted refusal deactivated, silently. With this
/// marker baked into the hosted binary, <see cref="HostedStartupContract"/> can tell "this IS the hosted
/// image" independently of the toggle, and REFUSE to start (rather than silently downgrade) whenever the
/// full hosted contract is not present.
///
/// It lives in <c>CcDirector.Gateway</c> (the shared library both hosts reference) so the shared
/// <see cref="GatewayEntryPoint"/> can read it off the entry assembly. Only the hosted host applies it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class HostedGatewayImageAttribute : Attribute
{
}
