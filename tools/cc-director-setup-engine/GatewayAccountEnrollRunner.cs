using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Setup.Engine;

/// <summary>
/// One gateway the installer discovered on the signed-in account (issue #1206): its human-readable
/// <paramref name="Name"/> (shown to the person - a raw URL is never shown) and its reachable front-door
/// <paramref name="EndpointUrl"/> (used as the enroll target). Only gateways that have published an address
/// are surfaced.
/// </summary>
/// <param name="Name">The gateway's device name, shown in the chooser when the account has more than one.</param>
/// <param name="EndpointUrl">The gateway's reachable front-door URL, used as the enroll target.</param>
public sealed record DiscoveredGateway(string Name, string EndpointUrl);

/// <summary>
/// The installer-time gateway-join gate for a Workstation install. A machine that joins an existing
/// fleet as a Workstation MUST connect to its gateway before the install can finish: the gateway is the
/// account authority, so a Workstation with no gateway connection is useless.
///
/// This replaces the old 4-digit pairing-code mechanism with DevThrottle account sign-in, so signing in
/// is the ONE way any machine registers (the Gateway install already signs in; this brings the
/// Workstation onto the same model - epic #1069, issue #1198). The 4-digit code is gone; the gateway URL
/// stays, because the cloud never learns a fleet's private network address (DevThrottle relays no
/// traffic), so the machine must still be told WHERE its gateway is - only the authorization changes.
///
/// The join reuses three proven pieces end to end, so no new cloud endpoint is introduced:
///   1. The same browser loopback sign-in the Gateway install uses (<see cref="LoopbackLoginListener"/>
///      + <see cref="FirstRunLoginCoordinator.BuildSignInUrl"/>): the user signs in on devthrottle.com
///      and the account access token is captured on a <c>127.0.0.1</c> callback. It is held in memory
///      ONLY and never persisted - a Workstation holds no account credential (the Gateway is the
///      authority, issue #642).
///   2. The account device registry (<see cref="DeviceRegistryClient.RegisterAsync"/>, the same call the
///      Gateway uses to self-register): this Workstation is registered as an account device and the cloud
///      issues its per-device key once.
///   3. The gateway enrollment seam (<c>POST /m/enroll</c>, the same endpoint the phone and Cockpit use):
///      the cloud device key is exchanged at the target gateway for a LOCAL, individually-revocable
///      device key. The gateway confirms (account-scoped) the key belongs to its OWN account, so a
///      Workstation signed into a DIFFERENT account is refused with a clear reason.
///
/// On success it persists the gateway URL + the issued LOCAL device key to <c>config.json</c> and the
/// credential file via <see cref="GatewayCredentialStore.SaveEnrolledKey"/> - unchanged downstream, so
/// the Director and the local cc-* tools connect on first run exactly as before.
///
/// It never throws for an expected failure (cancelled or failed sign-in, cloud registration failure,
/// unreachable gateway, wrong account, no key issued); it returns a human-readable reason so the
/// installer step renders it and BLOCKS completion. The sign-in step, the HTTP handler, and the persist
/// action are all injectable so the join logic is unit-testable with no browser, no network, and no disk.
/// The captured access token and both device keys are NEVER written to the log (security rule DT-05).
/// </summary>
public sealed class GatewayAccountEnrollRunner
{
    /// <summary>The account device-registry device type recorded for a Workstation/Director machine,
    /// distinct from the Gateway's own "gateway" type and the browser/phone types.</summary>
    public const string WorkstationDeviceType = "workstation";

    /// <summary>The account device-registry device type a Gateway registers itself as (issue #1206). The
    /// installer filters the account's devices to this type to discover which gateway to enroll against.</summary>
    public const string GatewayDeviceType = "gateway";

    /// <summary>The platform string reported for a Windows Workstation install (a roster attribute).</summary>
    public const string WorkstationPlatform = "windows";

    /// <summary>How long to wait for the browser sign-in hand-back before treating it as abandoned.
    /// Mirrors the installer's forced sign-in step (issue #657): long enough for a real sign-in, short
    /// enough to recover.</summary>
    public static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<DevThrottleTokens>> _signIn;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Action<string, string> _persist;
    private readonly TimeSpan _httpTimeout;

    // The account token captured by SignInAndDiscoverGatewaysAsync, held in memory ONLY for the immediately
    // following EnrollWithDiscoveredGatewayAsync call (issue #1206). A Workstation persists no account
    // credential; this is never written to disk or logged. Single-use per install wizard instance.
    private DevThrottleTokens? _pendingTokens;

    /// <summary>
    /// Build a runner. By default it drives a real browser loopback sign-in, makes real cloud/gateway HTTP
    /// calls, and persists with <see cref="GatewayCredentialStore.SaveEnrolledKey"/>. Tests inject a fake
    /// sign-in that returns a token with no browser, a fake HTTP handler, and a capturing persist action so
    /// the join logic runs with no network and no disk writes (no fallback construction - each has an
    /// explicit default).
    /// </summary>
    /// <param name="signIn">Performs the browser sign-in and returns the captured account token pair; null
    /// uses the real loopback + system-browser flow with a <see cref="DefaultSignInTimeout"/> deadline.</param>
    /// <param name="handlerFactory">Supplies the <see cref="HttpMessageHandler"/> for the cloud register and
    /// the gateway enroll calls; null uses a real <see cref="HttpClientHandler"/>.</param>
    /// <param name="persist">Persists the verified (gatewayUrl, localDeviceKey) pair; null uses
    /// <see cref="GatewayCredentialStore.SaveEnrolledKey"/>.</param>
    /// <param name="httpTimeout">Per-call timeout for the HTTP calls; null uses 15 seconds.</param>
    public GatewayAccountEnrollRunner(
        Func<CancellationToken, Task<DevThrottleTokens>>? signIn = null,
        Func<HttpMessageHandler>? handlerFactory = null,
        Action<string, string>? persist = null,
        TimeSpan? httpTimeout = null)
    {
        _signIn = signIn ?? SignInViaBrowserAsync;
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
        _persist = persist ?? GatewayCredentialStore.SaveEnrolledKey;
        _httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Sign in with the DevThrottle account, register this Workstation as an account device, exchange its
    /// cloud device key at the gateway at <paramref name="gatewayUrl"/> for a local device key, and on
    /// success persist the gateway URL + local device key. Returns the issued
    /// <see cref="MobileEnrollmentResponse"/> on success, or a human-readable reason on any expected
    /// failure. The installer gates the Finish button on <see cref="OperationResult{T}.Success"/>.
    /// </summary>
    /// <param name="gatewayUrl">The target gateway's reachable URL (for example a tailnet address).</param>
    /// <param name="deviceId">This machine's stable device/install id - used both as the cloud
    /// registration install id and as the <c>/m/enroll</c> device id, so the gateway's local record maps
    /// to the same cloud roster row.</param>
    /// <param name="machineName">A human-readable device name shown in the account roster.</param>
    /// <param name="ct">Cancelled by the installer's Cancel button while the sign-in is in flight.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> VerifyAndSaveAsync(
        string gatewayUrl, string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<MobileEnrollmentResponse>.Fail("Enter the gateway URL.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");
        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway URL is not valid. Use http://host:port or https://host:port.");

        var url = gatewayUrl.Trim();
        EngineLog.Write($"[GatewayAccountEnrollRunner] VerifyAndSaveAsync: gateway={url}, deviceId={deviceId}, machine={machineName}");

        // 1. Sign in with the DevThrottle account (browser loopback). The token is held in memory only and
        // never persisted - a Workstation holds no account credential.
        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] VerifyAndSaveAsync: sign-in cancelled before a credential arrived");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in was cancelled. Click \"Sign in to DevThrottle\" to try again.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] VerifyAndSaveAsync: sign-in failed: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not return a usable credential. Please try again.");

        return await RegisterAndEnrollAsync(tokens, url, deviceId, machineName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sign in with the DevThrottle account and discover the account's gateways so the installer can enroll
    /// against one WITHOUT the person typing a gateway URL (issue #1206). The account already knows which
    /// gateways belong to the user: each signed-in Gateway publishes its own reachable front-door URL as its
    /// device <c>endpoint_url</c>, so this signs in (the same browser loopback flow), lists the account's
    /// devices, and returns the ones of type "gateway" that carry a non-empty <c>endpoint_url</c>.
    ///
    /// The captured token is held in memory only for the immediately-following
    /// <see cref="EnrollWithDiscoveredGatewayAsync"/> call (a Workstation holds no account credential) and is
    /// never persisted or logged. Returns the discovered gateways (one or more) on success, or a clear,
    /// actionable reason when the sign-in did not complete or the account has NO reachable gateway yet - never
    /// an empty list dressed as success and never a fabricated address.
    /// </summary>
    /// <param name="ct">Cancelled by the installer's Cancel button while the sign-in is in flight.</param>
    public async Task<OperationResult<IReadOnlyList<DiscoveredGateway>>> SignInAndDiscoverGatewaysAsync(CancellationToken ct = default)
    {
        EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: starting account sign-in + gateway discovery");

        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: sign-in cancelled before a credential arrived");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in was cancelled. Click \"Sign in to DevThrottle\" to try again.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: sign-in failed: {ex.Message}");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in did not return a usable credential. Please try again.");

        IReadOnlyList<CloudDeviceRecord> devices;
        try
        {
            using var cloudHttp = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
            var registry = new DeviceRegistryClient(cloudHttp);
            devices = await registry.ListDevicesAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: listing account devices failed: {ex.Message}");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Signed in, but your DevThrottle account devices could not be read. Please check your connection and try again.");
        }

        // A gateway the installer can enroll against is a device of type "gateway" that has published a
        // reachable front-door URL. A gateway with no address yet is NOT offered (there is nowhere to enroll).
        var gateways = new List<DiscoveredGateway>();
        foreach (var device in devices)
        {
            if (string.Equals(device.DeviceType, GatewayDeviceType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(device.EndpointUrl))
            {
                gateways.Add(new DiscoveredGateway(device.Name, device.EndpointUrl.Trim()));
            }
        }

        if (gateways.Count == 0)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: no reachable gateway on this account");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "No reachable gateway is registered on your account yet. Start and sign in your gateway first, then run this installer.");
        }

        // Hold the token in memory for the enroll call that follows the person's gateway choice.
        _pendingTokens = tokens;
        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: discovered {gateways.Count} reachable gateway(s) on the account");
        return OperationResult<IReadOnlyList<DiscoveredGateway>>.Ok(gateways);
    }

    /// <summary>
    /// Register this Workstation on the account and enroll it at the gateway discovered by
    /// <see cref="SignInAndDiscoverGatewaysAsync"/>, using the token that call captured (issue #1206). This is
    /// the second half of the drop-the-URL-box flow: the person never types the address - it is the
    /// discovered gateway's own published <paramref name="gatewayUrl"/>. On success it persists the gateway
    /// URL + issued local device key exactly as the manual path did.
    ///
    /// Must be called after a successful <see cref="SignInAndDiscoverGatewaysAsync"/> in the same run; if no
    /// captured token is held it fails with a clear reason rather than silently signing in again.
    /// </summary>
    /// <param name="gatewayUrl">The discovered gateway's reachable front-door URL (never typed by the person).</param>
    /// <param name="deviceId">This machine's stable device/install id.</param>
    /// <param name="machineName">A human-readable device name shown in the account roster.</param>
    /// <param name="ct">Cancels the register/enroll HTTP calls.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> EnrollWithDiscoveredGatewayAsync(
        string gatewayUrl, string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<MobileEnrollmentResponse>.Fail("The discovered gateway has no address.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");
        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The discovered gateway address is not a valid URL.");

        var tokens = _pendingTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Please sign in to DevThrottle first.");

        var url = gatewayUrl.Trim();
        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollWithDiscoveredGatewayAsync: gateway={url}, deviceId={deviceId}, machine={machineName}");
        return await RegisterAndEnrollAsync(tokens, url, deviceId, machineName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared register-then-enroll-then-persist steps, run against an ALREADY-captured account token:
    /// (2) register this machine as an account device so the cloud issues its per-device key, (3) exchange
    /// that cloud key at the target gateway for a LOCAL device key via <c>/m/enroll</c>, and (4) persist the
    /// gateway URL + local key to config.json. Returns the issued <see cref="MobileEnrollmentResponse"/> on
    /// success, or a human-readable reason on any expected failure (no persist). The device keys are never
    /// logged (security rule DT-05).
    /// </summary>
    private async Task<OperationResult<MobileEnrollmentResponse>> RegisterAndEnrollAsync(
        DevThrottleTokens tokens, string url, string deviceId, string machineName, CancellationToken ct)
    {
        // 2. Register THIS machine as a device on the account (the same call the Gateway self-registers
        // with), so the cloud issues its per-device key. The account access token authorizes the call.
        CloudDeviceRegistrationResult cloud;
        try
        {
            using var cloudHttp = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
            var registry = new DeviceRegistryClient(cloudHttp);
            cloud = await registry.RegisterAsync(
                tokens.AccessToken,
                new CloudDeviceRegistrationRequest(deviceId, WorkstationPlatform, machineName, WorkstationDeviceType, AppVersion.Semver),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] RegisterAndEnrollAsync: cloud device registration failed: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Signed in, but this workstation could not be registered on your DevThrottle account. Please check your connection and try again.");
        }

        if (string.IsNullOrWhiteSpace(cloud.DeviceKey))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The account registered this workstation but returned no device key. Please try again.");

        // 3. Exchange the cloud device key at the target gateway for a LOCAL device key (the same
        // /m/enroll seam the phone and Cockpit use), then 4. persist the gateway URL + local key.
        var enroll = await EnrollAtGatewayAsync(url, cloud.DeviceKey, deviceId, machineName, ct).ConfigureAwait(false);
        if (!enroll.Success || enroll.Value is null)
            return enroll;

        _persist(url, enroll.Value.DeviceKey);
        EngineLog.Write($"[GatewayAccountEnrollRunner] RegisterAndEnrollAsync: persisted gateway url + local per-device key (machine={machineName})");
        return OperationResult<MobileEnrollmentResponse>.Ok(enroll.Value);
    }

    /// <summary>
    /// POST the cloud device key to the gateway's <c>/m/enroll</c> and map the reply to a
    /// success-or-reason result. A transport failure, a 403 (this workstation is not on the gateway's
    /// account), a 409 (the gateway itself is not signed in), any other non-2xx, or a 2xx with no local key
    /// all return a clear reason and NO key - so the install can never finish on an enrollment that did not
    /// actually issue a local device key. The device keys are never logged (security rule DT-05).
    /// </summary>
    private async Task<OperationResult<MobileEnrollmentResponse>> EnrollAtGatewayAsync(
        string gatewayUrl, string cloudDeviceKey, string deviceId, string machineName, CancellationToken ct)
    {
        var request = new MobileEnrollmentRequest
        {
            DeviceKey = cloudDeviceKey,
            DeviceId = deviceId,
            Name = machineName,
            Platform = WorkstationPlatform,
        };

        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("m/enroll", request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync transport FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"Could not reach the gateway at {gatewayUrl}. Check the URL and that the gateway is running.");
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync rejected: HTTP 403 (workstation not on the gateway's account)");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "This workstation is signed in to a different DevThrottle account than the gateway. Sign in with the same account the gateway uses, then try again.");
        }
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync rejected: HTTP 409 (gateway not signed in)");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway is not signed in to a DevThrottle account yet. Sign in on the gateway host, then try again.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"The gateway refused the enrollment: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        MobileEnrollmentResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<MobileEnrollmentResponse>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync: could not read reply: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway accepted the sign-in but its reply could not be read.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync: 2xx with no local device key in reply");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway accepted the sign-in but returned no device key.");
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync: local per-device key issued for machine={machineName}");
        return OperationResult<MobileEnrollmentResponse>.Ok(body);
    }

    /// <summary>
    /// The default sign-in: stand up a <see cref="LoopbackLoginListener"/> on <c>127.0.0.1</c>, open the
    /// system browser at the DevThrottle sign-in address carrying the loopback callback as the
    /// <c>redirect_uri</c>, and wait for the browser to hand the account token pair back. Honors the
    /// caller's cancellation (the Cancel button) and a <see cref="DefaultSignInTimeout"/> deadline so an
    /// abandoned sign-in is never a dead end. The token value is never logged.
    /// </summary>
    private static async Task<DevThrottleTokens> SignInViaBrowserAsync(CancellationToken ct)
    {
        using var listener = new LoopbackLoginListener();
        var signInUrl = FirstRunLoginCoordinator.BuildSignInUrl(listener.CallbackUrl);
        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInViaBrowserAsync: sign-in url={signInUrl}");

        Process.Start(new ProcessStartInfo(signInUrl) { UseShellExecute = true });

        using var timeoutSource = new CancellationTokenSource(DefaultSignInTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        return await listener.WaitForCredentialAsync(linked.Token).ConfigureAwait(false);
    }
}
