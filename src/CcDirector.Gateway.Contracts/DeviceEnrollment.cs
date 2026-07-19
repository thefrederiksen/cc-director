namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A co-located Director's request to enroll with its own Gateway using the DevThrottle account
/// sign-in instead of a pairing code (issue #1069). The Director POSTs this to
/// <c>/devices/enroll-signed-in</c>; the Gateway issues the Director's own per-device key - a fresh one
/// if this device is already enrolled, since the registry keeps only a hash of the key it issued and so
/// has no plaintext to hand back (issue #1878) - gated on the Gateway being signed in to DevThrottle AND the
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
/// The Gateway's response to a successful device enrollment. Carries the unique per-device key the
/// enrolling device writes to its local credential file. Shared by every enrollment path: the
/// co-located Director's <see cref="EnrollSignedInRequest"/>, and the mobile/browser flows.
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
