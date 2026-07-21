namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The mobile app's request to enroll this phone with the Gateway (issue #908). The phone signed in
/// on devthrottle.com, which registered it as a device on the account and returned its per-device key;
/// the phone POSTs that key here to <c>/mobile/enroll</c>. The Gateway confirms (account-scoped) that the
/// key belongs to its OWN signed-in account and, on a match, issues the phone a LOCAL device key it can
/// validate offline. The account session is NEVER sent here - only the per-device key - so a worst-case
/// leak costs one revocable device, not the account (the device-key-only hand-back decision).
/// </summary>
public sealed class MobileEnrollmentRequest
{
    /// <summary>
    /// The per-device key the phone received from devthrottle.com (a <c>dtd_</c> account device key).
    /// Verified against the Gateway's own account; never logged (security rule DT-05).
    /// </summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>
    /// The phone's stable, self-generated device id. It MUST be the same value the phone used as its
    /// install id when it registered on devthrottle.com, so the Gateway's local record maps to the same
    /// cloud roster row (revoke-down and last-seen match on it).
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>A human-readable device name shown in the roster (for example the phone model). Optional.</summary>
    public string Name { get; set; } = "";

    /// <summary>The phone's platform string ("android" / "ios"). Optional; a roster attribute only.</summary>
    public string Platform { get; set; } = "";
}

/// <summary>
/// The Gateway's response to a successful <see cref="MobileEnrollmentRequest"/> (issue #908): the LOCAL
/// per-device key the phone stores and sends as its Bearer from then on. This is a
/// Gateway-issued key (not the cloud <c>dtd_</c> key and not the master token), so the Gateway validates
/// it offline and it is individually revocable.
/// </summary>
public sealed class MobileEnrollmentResponse
{
    /// <summary>The local, individually-revocable per-device key the phone uses as its Bearer.</summary>
    public string DeviceKey { get; set; } = "";
}
