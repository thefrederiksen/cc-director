using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Mirrors this Gateway's locally-enrolled children (workstations/phones that joined by signing in to
/// the same DevThrottle account) up to the DevThrottle cloud account roster, and enforces account-page
/// revokes back down (Path B: enroll+mirror-up and revoke-pull-down).
///
/// The child's local device key (issued by <see cref="DeviceRegistry"/>) stays the source of truth and the
/// only admission credential; the cloud row is a read-mirror for visibility plus a revoke command. Only the
/// Gateway ever talks to the cloud - it uses its OWN account Bearer
/// (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>) to register each child under its
/// member. The cloud is idempotent per (member, install id) and reads install_id straight off the body
/// (no account-ownership check), so a child's stable local
/// device id doubles as its cloud install id and re-mirroring never creates a duplicate row.
///
/// Two behaviours:
/// <list type="bullet">
/// <item><b>Mirror up</b> - <see cref="MirrorChildUpAsync"/> registers one freshly-enrolled child with the
/// cloud (device_type "workstation"/"phone", the child's install id / platform / machine name) and records
/// the returned cloud roster id against the child. Fired best-effort on enrollment; a failure never blocks
/// or fails the enrollment (the child already has its local key) and the reconcile sweep recovers it.</item>
/// <item><b>Reconcile</b> - <see cref="ReconcileAsync"/> runs on the Gateway's periodic device sweep: it
/// mirrors up any child not yet mirrored, pulls the cloud roster (GET /devices returns only non-revoked
/// devices), drops the local pairing key of any child this Gateway mirrored that is now absent from the
/// roster (revoked on the account page), and advances each surviving child's last-seen with a heartbeat.
/// A device enrolled via <c>/mobile/enroll</c> is recorded with its cloud roster id exactly like a paired child,
/// so it is subject to the same revoke-down removal. Persistent reconcile failures are surfaced via
/// <see cref="HasPersistentReconcileFailure"/> (issue #924) rather than being retried forever in silence.
/// </item>
/// </list>
///
/// Graceful degradation (the #857 / #651 / #664 pattern): this service NEVER blocks or gates the Gateway.
/// When the Gateway is not signed in both entry points are a no-op. Every cloud call is contained behind a
/// boundary try/catch that only logs and lets the next sweep retry - the Gateway stays signed in and running.
///
/// Security rule DT-05: the cloud register response returns a per-device key that is IGNORED (the child's
/// credential is its local pairing key, not this) and is never stored or logged. The Gateway's account
/// access token is never logged either - only the request shape, cloud device ids (not secrets), and the
/// outcome are.
/// </summary>
public sealed class ChildDeviceMirrorService
{
    /// <summary>
    /// How many consecutive reconcile sweeps must fail on a real cloud error before the condition is
    /// treated as PERSISTENT and surfaced (issue #924). At the ~1-minute sweep this is ~3 minutes of the
    /// revoke-down path being unable to reach the cloud - long enough to rule out a single transient blip,
    /// short enough that a genuinely-stuck "revokes are not propagating" condition becomes visible quickly
    /// rather than being retried forever in silence.
    /// </summary>
    public const int PersistentReconcileFailureThreshold = 3;

    private readonly DevThrottleAccountService _account;
    private readonly DeviceRegistryClient _client;
    private readonly DeviceRegistry _devices;
    private readonly string? _appVersion;

    // Reconcile-failure visibility (issue #924): the revoke-down sweep used to swallow every cloud error
    // into a per-sweep log line and retry forever, so a persistently-broken sweep ("website revokes are
    // not reaching this Gateway") was invisible. These fields count consecutive real reconcile failures so
    // the Cockpit/tray can read a status and a distinct escalated log signal fires once it is persistent.
    // Written on the sweep (timer) thread and read by status callers, so guarded by _statusGate.
    private readonly object _statusGate = new();
    private int _consecutiveReconcileFailures;
    private string? _lastReconcileError;

    /// <summary>
    /// Creates the child-mirror coordinator.
    /// </summary>
    /// <param name="account">The Gateway-hosted credential service the egress Bearer is read from. Required.</param>
    /// <param name="client">The cloud device-registry client (the injectable egress seam). Required.</param>
    /// <param name="devices">The Gateway's local child registry (source of truth for children). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    public ChildDeviceMirrorService(
        DevThrottleAccountService account,
        DeviceRegistryClient client,
        DeviceRegistry devices,
        string? appVersion = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _appVersion = appVersion;
    }

    /// <summary>
    /// The number of consecutive reconcile sweeps that have failed on a real cloud error (issue #924),
    /// reset to zero by the next clean sweep. A read-only status the Cockpit/tray can poll. No network call.
    /// </summary>
    public int ConsecutiveReconcileFailures
    {
        get { lock (_statusGate) { return _consecutiveReconcileFailures; } }
    }

    /// <summary>
    /// The message of the most recent reconcile failure (issue #924), or null when the last sweep was clean.
    /// A user-safe reason (never a token or key - those are never in the exception messages we record here).
    /// </summary>
    public string? LastReconcileError
    {
        get { lock (_statusGate) { return _lastReconcileError; } }
    }

    /// <summary>
    /// True when the revoke-down path is PERSISTENTLY unable to propagate account-page revokes to this
    /// Gateway (issue #924) - so a stuck "website revokes are not reaching this device" condition is a
    /// visible status the Cockpit/tray can show, not a silent forever-retry. It is set by EITHER of the two
    /// ways the sweep can be persistently stuck:
    /// <list type="bullet">
    /// <item>the reconcile cloud call itself has failed on
    /// <see cref="PersistentReconcileFailureThreshold"/> or more consecutive sweeps; or</item>
    /// <item>the Gateway's account-token refresh is persistently failing
    /// (<see cref="DevThrottleAccountService.HasPersistentRefreshFailure"/>, the Phase 3 / issue #911
    /// signal) - the reconcile then has no usable token to pull the roster with and silently skips, which is
    /// exactly the invisible-forever case this surfaces.</item>
    /// </list>
    /// Cleared by the next clean sweep (and, for the refresh half, by the next successful token refresh). No
    /// network call.
    /// </summary>
    public bool HasPersistentReconcileFailure
    {
        get
        {
            if (_account.HasPersistentRefreshFailure)
                return true;
            lock (_statusGate)
            {
                return _consecutiveReconcileFailures >= PersistentReconcileFailureThreshold;
            }
        }
    }

    /// <summary>
    /// Mirrors one freshly-enrolled child up to the cloud roster (Diagram 2b). Best-effort and self-contained
    /// so it can be fired fire-and-forget from the enrollment endpoint: it owns its boundary try/catch and
    /// never throws to the caller. A no-op when the Gateway is not signed in, the child id is unknown, or the
    /// child is already mirrored (has a cloud id). On success it records the cloud roster id against the child
    /// so the revoke-diff can match it later. The register response's per-device key is ignored (DT-05).
    /// </summary>
    /// <param name="deviceId">The child's stable local device id (also its cloud install id).</param>
    /// <param name="ct">Cancels the cloud call.</param>
    public async Task MirrorChildUpAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            var token = _account.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write($"[ChildDeviceMirrorService] MirrorChildUpAsync: Gateway not signed in -> not mirroring child id={deviceId} (reconcile retries once signed in)");
                return;
            }

            var entry = FindChild(deviceId);
            if (entry is null)
            {
                FileLog.Write($"[ChildDeviceMirrorService] MirrorChildUpAsync: no local child id={deviceId} -> no-op");
                return;
            }
            if (!string.IsNullOrEmpty(entry.CloudDeviceId))
            {
                FileLog.Write($"[ChildDeviceMirrorService] MirrorChildUpAsync: child id={deviceId} already mirrored (cloud id={entry.CloudDeviceId}) -> no-op");
                return;
            }

            await MirrorOneAsync(entry, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Graceful degradation: a failed mirror must never break enrollment. Log and let the reconcile
            // sweep retry - the child already holds its local pairing key and works regardless.
            FileLog.Write($"[ChildDeviceMirrorService] MirrorChildUpAsync: mirror failed for child id={deviceId} (child still enrolled locally; reconcile retries): {ex.Message}");
        }
    }

    /// <summary>
    /// The periodic Path B reconcile, driven off the Gateway's device sweep. In order: (1) mirror up any child
    /// not yet on the cloud roster; (2) pull the roster (GET /devices = non-revoked only) and drop the local
    /// pairing key of any child this Gateway mirrored that is now absent (revoked on the account page,
    /// Diagram 2c); (3) advance each surviving mirrored child's last-seen with a heartbeat (a 404 there is a
    /// second revoke signal and also drops the child). A no-op when the Gateway is not signed in. Owns its
    /// boundary try/catch so a cloud failure only logs and the next sweep retries - it never throws to the
    /// timer. Per-child steps are individually guarded so one bad child never stops the sweep.
    /// </summary>
    /// <remarks>
    /// Guaranteed revocation-latency bound (issue #924): because this is PULL-based on the periodic sweep
    /// (no inbound cloud-to-Gateway push - a Non-goal of epic #916), a device revoked on the account page
    /// keeps working until the NEXT sweep runs. The bound is therefore ONE sweep interval (the Gateway's
    /// cron sweep, ~1 minute); a revoked device is refused within that window and not before. This bound is
    /// also documented in docs/architecture/mobile/DEVICE_AUTH_SECURITY_MODEL.md.
    /// </remarks>
    /// <param name="ct">Cancels the cloud calls.</param>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        try
        {
            var token = _account.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                // A genuinely signed-out Gateway has no reconcile failure of its own (the dead-token case is
                // surfaced separately by the account's persistent-refresh-failure signal, issue #911/#924),
                // so a clean sign-out clears any prior cloud-call failure streak.
                ClearReconcileFailure();
                FileLog.Write("[ChildDeviceMirrorService] ReconcileAsync: Gateway not signed in -> skipping child reconcile");
                return;
            }

            var children = _devices.MirrorSnapshot();
            if (children.Count == 0)
            {
                ClearReconcileFailure();
                FileLog.Write("[ChildDeviceMirrorService] ReconcileAsync: no local children -> nothing to reconcile");
                return;
            }

            // (1) Mirror-up pass: register any child that has no cloud id yet (a child enrolled while the
            //     cloud was down, or whose enroll-time mirror failed). Each is guarded so one failure does
            //     not stop the others.
            foreach (var child in children)
            {
                if (!string.IsNullOrEmpty(child.CloudDeviceId))
                    continue;
                try
                {
                    await MirrorOneAsync(child, token, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: mirror-up retry failed for child id={child.DeviceId} (retry next sweep): {ex.Message}");
                }
            }

            // (2) Revoke-down pass: pull the cloud roster (non-revoked) and drop any child THIS Gateway
            //     mirrored that is no longer on it. Scoped to children we mirrored (a persisted cloud id) so
            //     we never touch a device we did not create. Re-read the snapshot so it includes the ids just
            //     assigned in the mirror-up pass.
            var cloud = await _client.ListDevicesAsync(token, ct).ConfigureAwait(false);
            var liveCloudIds = new HashSet<string>(cloud.Select(d => d.Id), StringComparer.Ordinal);

            var afterMirror = _devices.MirrorSnapshot();
            foreach (var child in afterMirror)
            {
                if (string.IsNullOrEmpty(child.CloudDeviceId))
                    continue; // never mirrored (mirror-up still failing) - not a revoke, leave it for next sweep
                if (liveCloudIds.Contains(child.CloudDeviceId))
                    continue; // still on the roster - keep it

                if (_devices.Remove(child.DeviceId))
                    FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: child id={child.DeviceId} (cloud id={child.CloudDeviceId}) revoked on the account page -> dropped its local pairing key");
            }

            // (3) Last-seen pass: heartbeat each surviving mirrored child so the dashboard shows it fresh. A
            //     404 means the cloud no longer knows this install (revoked/forgotten between the list and now)
            //     -> drop the child too. Each child is guarded independently.
            foreach (var child in _devices.MirrorSnapshot())
            {
                if (string.IsNullOrEmpty(child.CloudDeviceId))
                    continue;
                try
                {
                    // A child device publishes no front-door address of its own (issue #1206), so endpoint_url
                    // is omitted; ct is named because it now follows the optional endpointUrl parameter.
                    var known = await _client.HeartbeatAsync(token, child.DeviceId, _appVersion, ct: ct).ConfigureAwait(false);
                    if (!known && _devices.Remove(child.DeviceId))
                        FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: child id={child.DeviceId} unknown to the cloud on heartbeat (404) -> dropped its local pairing key");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: heartbeat failed for child id={child.DeviceId} (retry next sweep): {ex.Message}");
                }
            }

            // Reaching here means the roster pull (the revoke-down source of truth) succeeded, so the
            // revoke-down path is healthy this sweep - clear any prior cloud-call failure streak (issue #924).
            ClearReconcileFailure();
        }
        catch (Exception ex)
        {
            // Graceful degradation (#857): a cloud failure must not crash, block, or gate the Gateway. Log
            // and let the next sweep retry - the Gateway stays signed in and running. Issue #924: record the
            // failure so a PERSISTENTLY-broken revoke-down path becomes a visible status + a distinct log
            // signal instead of an invisible forever-retry.
            RecordReconcileFailure(ex);
        }
    }

    /// <summary>
    /// Clears the consecutive-reconcile-failure streak after a clean sweep (issue #924). Logs a one-time
    /// recovery line when a previously-persistent failure has just cleared so the recovery is as visible as
    /// the escalation was.
    /// </summary>
    private void ClearReconcileFailure()
    {
        bool wasPersistent;
        lock (_statusGate)
        {
            wasPersistent = _consecutiveReconcileFailures >= PersistentReconcileFailureThreshold;
            _consecutiveReconcileFailures = 0;
            _lastReconcileError = null;
        }
        if (wasPersistent)
            FileLog.Write("[ChildDeviceMirrorService] ReconcileAsync: revoke-down path RECOVERED - the reconcile sweep reached the cloud roster again after a persistent failure");
    }

    /// <summary>
    /// Records one failed reconcile sweep (issue #924): advances the consecutive-failure counter, stores the
    /// message, and logs. The message never carries a token or key (those are not in the exception messages
    /// the reconcile can throw). Emits a DISTINCT escalated log signal on the sweep that first crosses
    /// <see cref="PersistentReconcileFailureThreshold"/>, so a stuck "revokes are not propagating" condition
    /// is a greppable, visible event rather than one indistinguishable retry line among many.
    /// </summary>
    private void RecordReconcileFailure(Exception ex)
    {
        int failures;
        bool justBecamePersistent;
        lock (_statusGate)
        {
            failures = ++_consecutiveReconcileFailures;
            _lastReconcileError = ex.Message;
            justBecamePersistent = failures == PersistentReconcileFailureThreshold;
        }

        FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: cloud reconcile failed (Gateway stays signed in and running; retry next sweep); consecutiveFailures={failures}: {ex.Message}");
        if (justBecamePersistent)
            FileLog.Write($"[ChildDeviceMirrorService] ReconcileAsync: PERSISTENT reconcile failure - the revoke-down sweep has failed {failures} times in a row and website device revokes are NOT reaching this Gateway; sign-in/connectivity needs attention (HasPersistentReconcileFailure is now true)");
    }

    /// <summary>
    /// Registers one child with the cloud and records the returned roster id against it. Throws on a cloud
    /// failure (the caller's boundary handles it). The register response's per-device key is discarded and
    /// never logged (DT-05); only the child's cloud roster id is persisted and logged.
    /// </summary>
    private async Task MirrorOneAsync(ChildMirrorEntry child, string token, CancellationToken ct)
    {
        FileLog.Write($"[ChildDeviceMirrorService] mirroring child id={child.DeviceId} up to the cloud roster (type={child.DeviceType}, platform={child.Platform})");
        var request = new CloudDeviceRegistrationRequest(
            child.DeviceId,
            child.Platform,
            child.MachineName,
            child.DeviceType,
            _appVersion);

        var result = await _client.RegisterAsync(token, request, ct).ConfigureAwait(false);
        // DT-05: result.DeviceKey (the cloud dtd_ key) is deliberately ignored - the child's credential is
        // its LOCAL pairing key. Persist only the roster id, used to match this child on the revoke-diff.
        _devices.SetCloudDeviceId(child.DeviceId, result.Device.Id);
    }

    /// <summary>Reads one child's mirror entry from the registry snapshot, or null when it is not present.</summary>
    private ChildMirrorEntry? FindChild(string deviceId)
    {
        foreach (var child in _devices.MirrorSnapshot())
        {
            if (string.Equals(child.DeviceId, deviceId, StringComparison.Ordinal))
                return child;
        }
        return null;
    }
}
