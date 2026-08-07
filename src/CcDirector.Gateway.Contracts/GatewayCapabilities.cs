namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What the Gateway on the other end of the tunnel can do, answered by <c>Hello</c>.
///
/// WHY THIS EXISTS. A Director and the Gateway it dials are deployed separately and update on
/// different days, so the Director routinely talks to a Gateway older than itself. Until now it had
/// no way to ask, and found out one hub method at a time, by calling one and being told "Method does
/// not exist" - which is a runtime error carrying no version, no list, and no way to know whether
/// anything else is missing too.
///
/// On 2026-08-05 that cost the whole fleet a morning. The hosted Gateway predated
/// <c>RegisterSessionKey</c>, so every Director happily minted a session key for every session it
/// launched, sent a registration that could never be accepted, and handed each agent a credential
/// the Gateway would refuse. Every agent's command line answered 401. Nothing said "this Gateway is
/// too old for me" because nothing had ever asked (#2457, #2459).
///
/// A NULL ANSWER IS A REAL ANSWER, and it is the reason this is shaped as a return value rather than
/// a new hub method. SignalR returns default (null) when a client invokes a hub method that returns
/// nothing, so <c>InvokeAsync&lt;GatewayCapabilities?&gt;("Hello", ...)</c> against a Gateway built
/// before this type simply yields null - meaning "old enough to predate capability reporting", which
/// is exactly the diagnosis worth having. It cost the older Gateway nothing and required no
/// negotiation, no version compare, and no second round trip. A new hub method would have failed in
/// precisely the way this exists to stop.
///
/// It is backward compatible in BOTH directions: an older Director calls the non-generic
/// <c>InvokeAsync("Hello", ...)</c> and discards the result, which is unchanged behaviour.
///
/// WHAT THIS IS NOT. It is not a feature gate. Nothing here decides whether the Director attempts a
/// call - the recovery paths already retry, and building a second decision on top of them would give
/// two answers to one question. It is diagnosis: the Director says plainly, once, at the point of
/// connection, what the Gateway it just reached cannot do.
/// </summary>
public sealed class GatewayCapabilities
{
    /// <summary>
    /// The Gateway's build version, so the two halves are comparable in one log line rather than by
    /// reading two machines' logs side by side.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// The Gateway's short commit, matching the <c>commit</c> field on <c>/healthz</c> - which is how
    /// a deploy is verified, so the same string identifies the running build from both directions.
    /// </summary>
    public string Commit { get; set; } = "";

    /// <summary>
    /// The hub methods this Gateway actually exposes to a Director. A NAMED LIST rather than a
    /// version number or a set of booleans, deliberately: the Director's question is never "which
    /// release is this" but "will the call I am about to make land", and a list answers that for
    /// methods added after this type was written without needing a new field for each one.
    /// </summary>
    public List<string> HubMethods { get; set; } = new();
}
