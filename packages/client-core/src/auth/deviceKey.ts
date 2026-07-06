// The shared per-device credential store (issue #908 for the phone, issue #1088 for the desktop
// Cockpit). The app never receives a master token from the page (the shell carries no secret).
// Instead it holds the per-device key it obtained by signing in on devthrottle.com and enrolling with
// the Gateway (POST /m/enroll). That key lives here, in localStorage, scoped to this origin (the
// Gateway), and is sent as the Bearer on every API call. Both shells share this one store: a browser
// enrolled through either shell is enrolled for the origin.
//
// A stable install id is generated once and persisted. It is the SAME value the app sends to
// devthrottle.com (install_id, when it registers the device) and to the Gateway (deviceId, at enroll),
// so the Gateway's local device record maps to the same cloud roster row - which is what lets a revoke
// on the website propagate down and drop the local key.

const DEVICE_KEY = "cc.deviceKey";
const INSTALL_ID = "cc.installId";

/** The stored per-device key, or "" when the phone has not enrolled yet. */
export function getDeviceKey(): string {
  try {
    return localStorage.getItem(DEVICE_KEY) ?? "";
  } catch {
    return "";
  }
}

/** Store the per-device key issued by the Gateway at enrollment. */
export function setDeviceKey(key: string): void {
  try {
    localStorage.setItem(DEVICE_KEY, key);
  } catch {
    /* storage unavailable (private mode) - the app will simply prompt to sign in again */
  }
}

/** Forget the per-device key (sign out, or the key was revoked and the Gateway now rejects it). */
export function clearDeviceKey(): void {
  try {
    localStorage.removeItem(DEVICE_KEY);
  } catch {
    /* ignore */
  }
}

/** True once the phone has enrolled and holds a per-device key. */
export function hasDeviceKey(): boolean {
  return getDeviceKey().length > 0;
}

/**
 * The phone's stable, self-generated install id, created once and persisted. Used as install_id at
 * devthrottle.com and as deviceId at /m/enroll so both sides map to one device. Falls back to a
 * non-persisted id only if storage is unavailable (the flow still works for this session).
 */
export function getInstallId(): string {
  try {
    let id = localStorage.getItem(INSTALL_ID);
    if (!id) {
      id = crypto.randomUUID();
      localStorage.setItem(INSTALL_ID, id);
    }
    return id;
  } catch {
    return crypto.randomUUID();
  }
}
