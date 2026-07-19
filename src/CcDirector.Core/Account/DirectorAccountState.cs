using System.Runtime.Versioning;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The Director's OWN local DevThrottle account state, as decided by <see cref="DirectorAccountStateProvider"/>
/// (two-step install, Slice A). This exists only for the gateway-less Director: when a gateway is
/// configured the Gateway is the single account authority (issue #642/#651) and this defers to it. When
/// no gateway is configured the Director legitimately holds and reads its own credential (the Slice A
/// exception to that centralization), so this reports whether it is signed in locally.
/// </summary>
public enum DirectorAccountState
{
    /// <summary>A gateway is configured, so the Director defers to the Gateway's account status (issue #651). This slice does not change the gateway-present display.</summary>
    DeferToGateway,

    /// <summary>No gateway is configured and the Director holds its own credential: signed in locally, with no gateway to use AI through yet.</summary>
    SignedInLocalNoGateway,

    /// <summary>No gateway is configured and no Director credential is present: not signed in.</summary>
    NotSignedIn,
}

/// <summary>
/// Decides the Director's own local account state (two-step install, Slice A) and provides the exact
/// display strings the account-status surface renders verbatim. This is the SINGLE place that rules the
/// no-gateway account state: a gateway-less Director owns this state itself (there is no Gateway to rule
/// it, so the dumb-client rule that governs Gateway verdicts does not apply here), and the surface only
/// renders what this provider decides.
///
/// The read seam revives the desktop credential service that issue #651 neutered: it builds the existing
/// <see cref="DevThrottleAccountService"/> through <see cref="DevThrottleAccountFactory.Build(IProtectedTokenStore, string)"/>
/// over the Director's own credential store and reads whether a credential is present. The token is never
/// read into a log and is never returned from this provider (security rule DT-05); only the resolved
/// state and a present/absent boolean ever leave it.
/// </summary>
public static class DirectorAccountStateProvider
{
    /// <summary>The account-line text for a gateway-less Director that IS signed in locally. Rendered verbatim by the surface.</summary>
    public const string SignedInLocalNoGatewayMessage =
        "Signed in to DevThrottle - connect a gateway to use AI.";

    /// <summary>The account-line text for a gateway-less Director that is NOT signed in. Rendered verbatim by the surface.</summary>
    public const string NoGatewayNotSignedInMessage =
        "No Gateway configured. Connect this Director to a Gateway on the Gateway tab.";

    /// <summary>
    /// The pure decision: with a gateway configured the Director defers to the Gateway; with no gateway
    /// it is signed in locally when (and only when) its own credential is present, otherwise not signed
    /// in. This is the production line the "reads+uses its own credential" revert-proof pins - making it
    /// ignore <paramref name="directorCredentialPresent"/> reds the no-gateway signed-in test.
    /// </summary>
    public static DirectorAccountState Resolve(bool gatewayConfigured, bool directorCredentialPresent)
    {
        if (gatewayConfigured)
            return DirectorAccountState.DeferToGateway;

        return directorCredentialPresent
            ? DirectorAccountState.SignedInLocalNoGateway
            : DirectorAccountState.NotSignedIn;
    }

    /// <summary>
    /// Resolves the Director's account state over an explicit credential store (the unit-testable seam).
    /// With a gateway configured it returns <see cref="DirectorAccountState.DeferToGateway"/> without
    /// reading the local credential. With no gateway it revives the desktop credential service over the
    /// supplied store and reads whether a credential is present. The stored token is never logged or
    /// returned (DT-05); this reads only presence and, when present, exercises the revived local read by
    /// decoding the cached identity claims (no network, no token in the log) so the seam is proven live.
    /// </summary>
    public static DirectorAccountState ResolveFromStore(GatewayConfig config, IProtectedTokenStore store)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));
        if (store is null)
            throw new ArgumentNullException(nameof(store));

        if (config.IsEnabled)
        {
            FileLog.Write("[DirectorAccountStateProvider] ResolveFromStore: a gateway is configured -> defer to the Gateway account authority (issue #642/#651)");
            return DirectorAccountState.DeferToGateway;
        }

        // Revive the desktop credential service (dead since issue #651): the factory reconstitutes the
        // validator, event log, and refresher over the Director's OWN store, so a gateway-less Director
        // can read the credential it now legitimately keeps (Slice A). No network at construction.
        var service = DevThrottleAccountFactory.Build(store, CcStorage.DevThrottleAuthEventsLog());

        var present = store.HasTokens;
        if (present)
        {
            // Exercise the revived read end to end without exposing the token: GetIdentity decodes the
            // cached claims locally (no network call, and the token is never written to the log; DT-05),
            // which confirms the read seam is live for the signed-in-local Director.
            var identity = service.GetIdentity();
            FileLog.Write($"[DirectorAccountStateProvider] ResolveFromStore: no gateway; director credential present (identity {(identity is null ? "unavailable" : "resolved")})");
        }
        else
        {
            FileLog.Write("[DirectorAccountStateProvider] ResolveFromStore: no gateway and no director credential present -> not signed in");
        }

        return Resolve(config.IsEnabled, present);
    }

    /// <summary>
    /// Resolves the Director's account state on Windows, reading the Director's own DPAPI-protected
    /// credential store. Windows-guarded because Windows Data Protection is per-user and Windows-only;
    /// the caller must guard with <see cref="OperatingSystem.IsWindows"/>. Fails loud with no fallback:
    /// a genuine read failure surfaces to the caller rather than masquerading as "not signed in".
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DirectorAccountState ResolveForWindows(GatewayConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var store = new WindowsProtectedTokenStore(CcStorage.DevThrottleCredentialBlob());
        return ResolveFromStore(config, store);
    }

    /// <summary>
    /// The exact display string the no-gateway account-status surface renders verbatim for a resolved
    /// state. Only the two no-gateway states have a Director-owned line; a gateway-configured Director
    /// defers to the Gateway status path instead, so passing <see cref="DirectorAccountState.DeferToGateway"/>
    /// is a programming error.
    /// </summary>
    public static string DescribeNoGateway(DirectorAccountState state) => state switch
    {
        DirectorAccountState.SignedInLocalNoGateway => SignedInLocalNoGatewayMessage,
        DirectorAccountState.NotSignedIn => NoGatewayNotSignedInMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state,
            "DescribeNoGateway is only for the no-gateway states; a gateway-configured Director defers to the Gateway status."),
    };
}
