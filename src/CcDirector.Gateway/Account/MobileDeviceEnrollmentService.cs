using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Enrolls a device with this Gateway from a per-device key the device obtained by signing in on
/// devthrottle.com (issue #908 for the phone; generalized for the desktop Cockpit browser in issue
/// #1088 - the platform decides the recorded device type, see <see cref="DeviceTypeForPlatform"/>).
/// This is the bridge that lets a browser shell authenticate to the local Gateway WITHOUT the Gateway
/// ever handing the browser the master token, and WITHOUT the cloud sitting in the request path:
///
/// <list type="number">
/// <item>The device signs in on devthrottle.com, which registers it as a device on the account and
/// returns its per-device (<c>dtd_</c>) key. Only that key is handed back to the device - never the
/// account session (the device-key-only decision), so a worst-case leak costs one revocable device.</item>
/// <item>The device POSTs the key here. This service confirms, ACCOUNT-SCOPED, that the key is a live
/// device on the Gateway's OWN signed-in account (<see cref="DeviceRegistryClient.VerifyDeviceKeyAsync"/>,
/// a hash match under the Gateway's member - not a masked prefix/last-four compare, which has too few
/// bits to resist a guess).</item>
/// <item>On a match it issues the device a LOCAL device key (<see cref="DeviceRegistry"/>) the Gateway
/// validates offline on every later request, and records the cloud roster id against it so the existing
/// Path B revoke-down sweep (<see cref="ChildDeviceMirrorService"/>) drops the local key when the device
/// is revoked from "Your devices".</item>
/// </list>
///
/// The verify is the ONLY cloud touch on the enrollment path; every request the device makes afterward is
/// validated locally. The device key and the account token are never written to the log (security rule
/// DT-05). This type holds no try/catch: a cloud failure from the verify propagates to the endpoint
/// boundary, which is the single place that translates it to an error response.
/// </summary>
public sealed class MobileDeviceEnrollmentService
{
    /// <summary>The device type recorded for an enrolled phone (a roster attribute, never a credential).</summary>
    public const string PhoneDeviceType = "phone";

    /// <summary>The device type recorded for an enrolled desktop browser (issue #1088).</summary>
    public const string BrowserDeviceType = "browser";

    /// <summary>
    /// The device type a platform value maps to (issue #1088): the phone platforms ("android"/"ios")
    /// stay <see cref="PhoneDeviceType"/>, and a MISSING platform also stays phone (every pre-#1088
    /// enrollee is a phone, and an old mobile build could omit the field); anything else - the desktop
    /// Cockpit sends "browser" - is recorded as <see cref="BrowserDeviceType"/>. A roster attribute
    /// only, never part of any credential check.
    /// </summary>
    public static string DeviceTypeForPlatform(string? platform)
    {
        var value = (platform ?? "").Trim();
        if (value.Length == 0
            || value.Equals("android", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ios", StringComparison.OrdinalIgnoreCase))
        {
            return PhoneDeviceType;
        }
        return BrowserDeviceType;
    }

    private readonly DevThrottleAccountService? _account;
    private readonly DeviceRegistryClient _cloud;
    private readonly DeviceRegistry _devices;

    /// <summary>
    /// Creates the enrollment service.
    /// </summary>
    /// <param name="account">
    /// The Gateway-hosted account credential service, whose access token authorizes the account-scoped
    /// verify. Null on a host with no credential service (a non-Windows host); enrollment then reports an
    /// explicit "not signed in" outcome rather than proceeding.
    /// </param>
    /// <param name="cloud">The cloud device-registry client (the injectable egress seam). Required.</param>
    /// <param name="devices">The Gateway's local device registry that issues and validates the local key. Required.</param>
    public MobileDeviceEnrollmentService(DevThrottleAccountService? account, DeviceRegistryClient cloud, DeviceRegistry devices)
    {
        _account = account;
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>
    /// Enrolls the device from its per-device key. Returns a <see cref="MobileEnrollmentOutcome"/> the
    /// endpoint maps to an HTTP status: <c>BadRequest</c> for missing inputs, <c>NotSignedIn</c> when the
    /// Gateway holds no account credential to verify against, <c>Rejected</c> when the key is not a live
    /// device on this Gateway's account, and <c>Ok</c> (carrying the issued local key) on success. The
    /// recorded device type follows the platform (issue #1088): android/ios enroll as a phone, anything
    /// else (the desktop Cockpit sends "browser") as a browser. A cloud failure during verify is NOT
    /// caught here - it propagates to the endpoint boundary.
    /// </summary>
    public async Task<MobileEnrollmentOutcome> EnrollAsync(
        string? deviceKey, string? deviceId, string? name, string? platform, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            return MobileEnrollmentOutcome.BadRequest("deviceKey is required");
        if (string.IsNullOrWhiteSpace(deviceId))
            return MobileEnrollmentOutcome.BadRequest("deviceId is required");
        var id = deviceId.Trim();

        var token = _account?.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write($"[MobileDeviceEnrollmentService] EnrollAsync: Gateway not signed in -> cannot verify device key for deviceId={id}");
            return MobileEnrollmentOutcome.NotSignedIn();
        }

        var cloudDeviceId = await _cloud.VerifyDeviceKeyAsync(token, deviceKey, ct).ConfigureAwait(false);
        if (cloudDeviceId is null)
        {
            FileLog.Write($"[MobileDeviceEnrollmentService] EnrollAsync: device key is not a live device on this Gateway's account -> rejected (deviceId={id})");
            return MobileEnrollmentOutcome.Rejected();
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        var deviceType = DeviceTypeForPlatform(platform);
        var registration = _devices.Register(id, displayName, platform, deviceType);
        // Map the local device to its cloud roster row so the existing Path B revoke-down sweep drops the
        // local key when the device is revoked from "Your devices" (the cloud row is the source of the revoke).
        _devices.SetCloudDeviceId(id, cloudDeviceId);

        FileLog.Write($"[MobileDeviceEnrollmentService] EnrollAsync: enrolled {deviceType} deviceId={id}, cloud id={cloudDeviceId} -> issued a local device key (key not logged)");
        return MobileEnrollmentOutcome.Ok(registration.DeviceKey);
    }
}

/// <summary>
/// The result of <see cref="MobileDeviceEnrollmentService.EnrollAsync"/>: a small tagged outcome the
/// endpoint boundary maps to an HTTP status. The issued local key is present only on <see cref="Kind"/>
/// == <see cref="ResultKind.Ok"/>; the message is a user-safe reason on the failure kinds.
/// </summary>
public sealed class MobileEnrollmentOutcome
{
    /// <summary>The kind of outcome, mapped by the endpoint to an HTTP status.</summary>
    public enum ResultKind
    {
        /// <summary>Enrolled; <see cref="LocalDeviceKey"/> carries the issued local key.</summary>
        Ok,

        /// <summary>A required input was missing or blank (maps to 400).</summary>
        BadRequest,

        /// <summary>The Gateway holds no account credential to verify against (maps to 409).</summary>
        NotSignedIn,

        /// <summary>The key is not a live device on this Gateway's account (maps to 403).</summary>
        Rejected,
    }

    private MobileEnrollmentOutcome(ResultKind kind, string? localDeviceKey, string? message)
    {
        Kind = kind;
        LocalDeviceKey = localDeviceKey;
        Message = message;
    }

    /// <summary>The outcome kind.</summary>
    public ResultKind Kind { get; }

    /// <summary>The issued local per-device key, present only when <see cref="Kind"/> is <see cref="ResultKind.Ok"/>.</summary>
    public string? LocalDeviceKey { get; }

    /// <summary>A user-safe reason on a failure kind, or null on success.</summary>
    public string? Message { get; }

    /// <summary>Enrolled: carries the issued local per-device key.</summary>
    public static MobileEnrollmentOutcome Ok(string localDeviceKey) => new(ResultKind.Ok, localDeviceKey, null);

    /// <summary>A required input was missing or blank.</summary>
    public static MobileEnrollmentOutcome BadRequest(string message) => new(ResultKind.BadRequest, null, message);

    /// <summary>The Gateway is not signed in, so it cannot verify the device key.</summary>
    public static MobileEnrollmentOutcome NotSignedIn() => new(
        ResultKind.NotSignedIn, null,
        "This Gateway is not signed in to a DevThrottle account, so it cannot enroll a device. Sign the Gateway in and try again.");

    /// <summary>The device key is not a live device on this Gateway's account.</summary>
    public static MobileEnrollmentOutcome Rejected() => new(
        ResultKind.Rejected, null,
        "This device is not registered to this Gateway's account. Sign in on devthrottle.com with the same account this Gateway uses, then try again.");
}
