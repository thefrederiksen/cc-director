namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The Director's request to enroll with a Gateway using a pairing code (issue #469).
/// The Director reads the 4-digit code off the Gateway host's local window (Anchor B - the
/// code never crosses the network), then POSTs this to <c>/devices/register</c>. The Gateway
/// verifies the code (matches, not expired, not already used) and, on success, issues a unique
/// per-device key recorded in its device registry.
/// </summary>
public sealed class DeviceRegistrationRequest
{
    /// <summary>The Director's stable device identity (its existing device GUID).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The new machine's name, recorded in the registry and echoed back for confirmation.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The 4-digit pairing code read off the Gateway host's local window.</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>
    /// The child's operating-system platform string (for example "windows", "android"), sent so the
    /// Gateway can mirror the child up to the cloud account roster with the right platform (Path B,
    /// device-gateway-topology.md Diagram 2b). Optional; empty when a child app predates this field
    /// (the mirror then records "unknown"). The workstation/phone apps supplying it are the child-side
    /// work.
    /// </summary>
    public string Platform { get; set; } = "";

    /// <summary>
    /// The child's device type - "workstation" or "phone" - sent so the Gateway mirrors the child up to
    /// the cloud roster with the correct type. Optional; empty when a child app predates this field, in
    /// which case the Gateway defaults it to "workstation". This is a display/roster attribute only, never
    /// an admission credential (the child's local pairing key is).
    /// </summary>
    public string DeviceType { get; set; } = "";
}

/// <summary>
/// A co-located Director's request to enroll with its own Gateway using the DevThrottle account
/// sign-in instead of a pairing code (issue #1069). The Director POSTs this to
/// <c>/devices/enroll-signed-in</c>; the Gateway mints (or, if this device already has one, returns)
/// the Director's own per-device key - gated on the Gateway being signed in to DevThrottle AND the
/// caller being a loopback same-machine connection. There is no pairing code: signing in is the
/// authorization. Carries no credential of its own; the loopback origin plus the Gateway's signed-in
/// account are the proof.
/// </summary>
public sealed class EnrollSignedInRequest
{
    /// <summary>The Director's stable device identity (its existing device GUID).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The Director's machine name, recorded in the registry and echoed back for confirmation.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The operating-system platform string (for example "windows"), for the cloud mirror roster.</summary>
    public string Platform { get; set; } = "";

    /// <summary>The device type - defaults to "workstation" - for the cloud mirror roster.</summary>
    public string DeviceType { get; set; } = "";
}

/// <summary>
/// The Gateway's response to a successful <see cref="DeviceRegistrationRequest"/> (issue #469).
/// Carries the unique per-device key the Director writes to its local credential file. The
/// pairing code is consumed and never returned.
/// </summary>
public sealed class DeviceRegistrationResponse
{
    /// <summary>The unique, individually-revocable per-device key issued by the Gateway.</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>The device id that was registered (echo of the request).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The machine name that was registered (echo of the request).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The device's status in the registry at issue time (e.g. <c>active</c>).</summary>
    public string Status { get; set; } = "";

    /// <summary>How many devices are registered after this enrollment (host confirmation message).</summary>
    public int DeviceCount { get; set; }
}

/// <summary>
/// One device's public-facing entry in the Gateway device registry (issue #469): the
/// host-readable record used to list registered devices. The per-device key itself is NEVER
/// included - the registry serves identity and status, not the secret.
/// </summary>
public sealed class RegisteredDeviceDto
{
    /// <summary>The device's stable identity.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The device's machine name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>When the per-device key was issued (UTC).</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>The device's status (e.g. <c>active</c>, <c>revoked</c>).</summary>
    public string Status { get; set; } = "";
}
