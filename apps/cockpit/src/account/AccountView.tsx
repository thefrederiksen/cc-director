import { useCallback, useEffect, useRef, useState } from "react";
import {
  getAccountStatus,
  logoutAccount,
  getAccountDevices,
  removeAccountDevice,
  startSignIn,
  type AccountStatus,
  type AccountDevice,
  type AccountDevicesResponse,
} from "@devthrottle/client-core/account/accountClient";

// The Account page (issue #978, epic #967) - the React port of the Blazor Cockpit Account.razor
// (#853/#648/#854). A pure client of the Gateway account endpoints: the credential lives on the
// Gateway and the raw token is NEVER fetched, stored, or displayed (security rule DT-05).
//   - signed-out: a real "Sign in" action that starts the Gateway browser loopback sign-in via
//     POST /account/sign-in, then polls GET /account/status for completion;
//   - signed-in: identity + Log out (POST /account/logout) AND a "Your devices" list from
//     GET /account/devices with a per-device Remove (DELETE /account/devices/{id}).
// Responsive (CodingStyle.md): the page renders immediately with a loading state and loads the status +
// device list asynchronously. On any failure it shows an explicit error state, never a fabricated
// signed-out or empty-devices view (the no-fallback rule).

/** How long the page waits for the browser sign-in to complete before it stops polling. */
const SIGN_IN_POLL_TIMEOUT_MS = 5 * 60 * 1000;
/** How often the page re-reads the Gateway status while a sign-in is in flight. */
const SIGN_IN_POLL_INTERVAL_MS = 2000;

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="acct-row">
      <div className="acct-row-label">{label}</div>
      <div className="acct-row-value">{value}</div>
    </div>
  );
}

function deviceMeta(device: AccountDevice): string {
  const parts: string[] = [];
  if (device.platform && device.platform.length > 0) parts.push(device.platform);
  if (device.deviceType && device.deviceType.length > 0) parts.push(device.deviceType);
  if (device.appVersion && device.appVersion.length > 0) parts.push(`v${device.appVersion}`);
  return parts.length === 0 ? "Unknown device" : parts.join(" - ");
}

// Format a cloud timestamp to the compact "yyyy-MM-dd HH:mm" local-time string; "unknown" when absent,
// and the raw value verbatim when unparseable (the cloud value is the source of truth - never fabricate
// a time).
function formatTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value.trim().length === 0) return "unknown";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())} ` +
    `${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`
  );
}

export function AccountView() {
  const [status, setStatus] = useState<AccountStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loggedOut, setLoggedOut] = useState(false);

  // Sign-in (signed-out state).
  const [signInInProgress, setSignInInProgress] = useState(false);
  const [signInError, setSignInError] = useState<string | null>(null);

  // Devices (signed-in state).
  const [devices, setDevices] = useState<AccountDevicesResponse | null>(null);
  const [devicesLoading, setDevicesLoading] = useState(false);
  const [devicesError, setDevicesError] = useState<string | null>(null);
  const [confirmRemoveId, setConfirmRemoveId] = useState<string | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removedNote, setRemovedNote] = useState<string | null>(null);

  // The in-flight sign-in poll is cancelled on unmount and whenever a new sign-in starts, so a poll
  // never survives the component or races a second attempt.
  const pollAbortRef = useRef<AbortController | null>(null);

  const loadDevices = useCallback(async (signal?: AbortSignal) => {
    setDevicesLoading(true);
    setDevicesError(null);
    setConfirmRemoveId(null);
    try {
      setDevices(await getAccountDevices(signal));
    } catch (err) {
      if (signal?.aborted) return;
      // No-fallback: a Gateway/cloud error surfaces as an explicit error state, never an empty list.
      setDevices(null);
      setDevicesError(err instanceof Error ? err.message : "Failed to load your devices");
    } finally {
      setDevicesLoading(false);
    }
  }, []);

  const loadStatus = useCallback(
    async (signal?: AbortSignal) => {
      try {
        setError(null);
        const next = await getAccountStatus(signal);
        setStatus(next);
        if (next.signedIn) await loadDevices(signal);
      } catch (err) {
        if (signal?.aborted) return;
        setError(err instanceof Error ? err.message : "Failed to load account status");
      }
    },
    [loadDevices],
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadStatus(controller.signal);
    return () => {
      controller.abort();
      pollAbortRef.current?.abort();
    };
  }, [loadStatus]);

  const logOut = async () => {
    if (busy) return;
    setBusy(true);
    setLoggedOut(false);
    try {
      setStatus(await logoutAccount());
      setLoggedOut(true);
      // The device section belongs to the signed-in view; drop it so a later sign-in reloads fresh.
      setDevices(null);
      setDevicesError(null);
      setConfirmRemoveId(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Logout failed");
    } finally {
      setBusy(false);
    }
  };

  const requestRemove = (deviceId: string) => {
    setConfirmRemoveId(deviceId);
    setRemoveError(null);
    setRemovedNote(null);
  };

  const cancelRemove = () => setConfirmRemoveId(null);

  const confirmRemove = async (deviceId: string) => {
    if (removeBusy) return;
    setRemoveBusy(true);
    setRemoveError(null);
    setRemovedNote(null);
    try {
      await removeAccountDevice(deviceId);
      setConfirmRemoveId(null);
      setRemovedNote("Device removed.");
      // Refresh from the source so the row disappears and the list reflects the account exactly.
      await loadDevices();
    } catch (err) {
      setRemoveError(err instanceof Error ? err.message : "Failed to remove the device");
    } finally {
      setRemoveBusy(false);
    }
  };

  // Poll the Gateway status while the browser sign-in runs, until the Gateway reports signed-in or the
  // poll window elapses. The token hand-back happens on the Gateway (#637); this page only observes the
  // resulting status - it never sees the credential.
  const pollUntilSignedIn = useCallback(async () => {
    pollAbortRef.current?.abort();
    const controller = new AbortController();
    pollAbortRef.current = controller;
    const signal = controller.signal;
    const deadline = Date.now() + SIGN_IN_POLL_TIMEOUT_MS;

    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, SIGN_IN_POLL_INTERVAL_MS));
      if (signal.aborted) return;
      let next: AccountStatus;
      try {
        next = await getAccountStatus(signal);
      } catch {
        // A transient status read failure during sign-in is expected (the Gateway is busy with the
        // hand-off); keep polling until the deadline rather than aborting the sign-in.
        continue;
      }
      if (next.signedIn) {
        setStatus(next);
        setSignInInProgress(false);
        await loadDevices(signal);
        return;
      }
    }

    // The poll window elapsed without a signed-in status: report it explicitly so the person can retry
    // rather than leaving the button stuck on "waiting".
    if (!signal.aborted) {
      setSignInInProgress(false);
      setSignInError("Sign-in did not complete. Finish it in your browser, then try again.");
    }
  }, [loadDevices]);

  const startSignInFlow = async () => {
    if (signInInProgress) return;
    setSignInError(null);
    setLoggedOut(false);
    try {
      const result = await startSignIn();
      if (result.alreadySignedIn) {
        await loadStatus();
        return;
      }
      if (!result.started) {
        setSignInError(result.error ?? "Sign-in could not be started.");
        return;
      }
      // Show "waiting for your browser" immediately and poll in the background (responsive UI).
      setSignInInProgress(true);
      void pollUntilSignedIn();
    } catch (err) {
      setSignInInProgress(false);
      setSignInError(err instanceof Error ? err.message : "Sign-in failed to start");
    }
  };

  return (
    <div className="page acct">
      <div className="page-head">
        <h1>DevThrottle Account</h1>
      </div>
      <p className="acct-lede">
        The DevThrottle identity this Gateway is signed in as, and the devices registered to the account.
        The credential lives on the Gateway; this page never sees the raw token.
      </p>

      {error !== null ? (
        <div className="acct-error">Could not load account status from the Gateway: {error}</div>
      ) : status === null ? (
        <p className="acct-loading">Loading...</p>
      ) : status.signedIn ? (
        <>
          <div className="acct-card">
            <div className="acct-state">
              <span className="acct-dot on" />
              <span className="acct-state-label on">Signed in</span>
            </div>
            <Row label="Email" value={status.email && status.email.length > 0 ? status.email : "(not available)"} />
            <Row
              label="Provider"
              value={status.provider && status.provider.length > 0 ? status.provider : "(not available)"}
            />
          </div>

          <div className="acct-logout-row">
            <button className="acct-btn primary" onClick={() => void logOut()} disabled={busy}>
              {busy ? "Logging out..." : "Log out"}
            </button>
            <span className="acct-note">
              Clears the Gateway credential and returns the Gateway to its sign-in prompt.
            </span>
          </div>

          <div className="acct-devices">
            <div className="acct-devices-head">
              <h2>Your devices</h2>
              <button
                className="acct-btn"
                onClick={() => void loadDevices()}
                disabled={devicesLoading}
              >
                Refresh
              </button>
            </div>
            <p className="acct-devices-sub">
              Every device registered to this DevThrottle account. Remove a device to revoke its access.
            </p>

            {devicesLoading ? (
              <p className="acct-devices-loading">Loading devices...</p>
            ) : devicesError !== null ? (
              <div className="acct-devices-error">
                Could not load your devices from the Gateway: {devicesError}
                <div className="acct-devices-retry-row">
                  <button className="acct-btn" onClick={() => void loadDevices()}>
                    Try again
                  </button>
                </div>
              </div>
            ) : devices !== null && !devices.signedIn ? (
              <div className="acct-devices-signedout">
                The Gateway reports it is no longer signed in, so the device list is unavailable. Sign in
                again to see your devices.
              </div>
            ) : devices !== null && (devices.devices?.length ?? 0) === 0 ? (
              <div className="acct-devices-empty">No devices are registered to this account yet.</div>
            ) : devices !== null && devices.devices ? (
              <div className="acct-devices-list">
                {devices.devices.map((device) => (
                  <div key={device.id} className="acct-device-row">
                    <div className="acct-device-main">
                      <div className="acct-device-name-row">
                        <span className="acct-device-name">
                          {device.name.length > 0 ? device.name : "(unnamed device)"}
                        </span>
                        {device.thisDevice && <span className="acct-this-device">This device</span>}
                      </div>
                      <div className="acct-device-meta">
                        <span>{deviceMeta(device)}</span>
                        <span className="acct-device-sep">|</span>
                        <span>Last seen: {formatTimestamp(device.lastSeenAt)}</span>
                      </div>
                    </div>

                    {confirmRemoveId === device.id ? (
                      <div className="acct-remove-confirm">
                        <span className="acct-remove-ask">Remove this device?</span>
                        <button
                          className="acct-btn danger"
                          onClick={() => void confirmRemove(device.id)}
                          disabled={removeBusy}
                        >
                          {removeBusy ? "Removing..." : "Remove"}
                        </button>
                        <button className="acct-btn" onClick={cancelRemove} disabled={removeBusy}>
                          Cancel
                        </button>
                      </div>
                    ) : (
                      <button
                        className="acct-btn"
                        onClick={() => requestRemove(device.id)}
                        disabled={confirmRemoveId !== null}
                      >
                        Remove
                      </button>
                    )}
                  </div>
                ))}
              </div>
            ) : null}

            {removeError !== null && (
              <div className="acct-remove-error">Could not remove the device: {removeError}</div>
            )}
            {removedNote !== null && <div className="acct-remove-ok">{removedNote}</div>}
          </div>
        </>
      ) : (
        <div className="acct-card">
          <div className="acct-state">
            <span className="acct-dot" />
            <span className="acct-state-label">Not signed in</span>
          </div>
          <p className="acct-signedout-explain">
            This Gateway is not signed in to DevThrottle. Sign in to register this device and manage your
            account.
          </p>
          <button className="acct-btn signin" onClick={() => void startSignInFlow()} disabled={signInInProgress}>
            {signInInProgress ? "Waiting for your browser..." : "Sign in to DevThrottle"}
          </button>
          <p className="acct-note acct-signin-note">
            Opens your web browser to finish signing in with Google, GitHub, or email.
          </p>

          {signInInProgress && (
            <div className="acct-signin-progress">
              Finish signing in in the browser window that opened. This page updates automatically once
              you are signed in.
            </div>
          )}
          {signInError !== null && <div className="acct-signin-error">{signInError}</div>}
        </div>
      )}

      {loggedOut && <div className="acct-loggedout">Logged out. The Gateway credential was cleared.</div>}
    </div>
  );
}
