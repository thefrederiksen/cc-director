// Shared helpers for building the device-enrollment request a shell sends to devthrottle.com and for
// verifying the round trip when it returns (issue #908, generalized for the desktop Cockpit browser in
// issue #1088).
//
// TWO shells share this one flow: the mobile PWA (served under /mobile) and the desktop Cockpit (served at
// the site root). Each shell installs its own EnrollmentShellProfile at startup via
// configureEnrollment - the same per-shell injection pattern configureUnauthorizedRedirect uses
// (issue #1024) - so shared client-core hardcodes neither shell's routes or device identity. The
// MOBILE profile is the default, so an unconfigured client-core behaves exactly as the phone always
// has (the phone flow is the proven reference and must not regress).
//
// The base is devthrottle.com in production; a dev build can point at a local site with
// VITE_DT_SITE_BASE (for example http://localhost:5173) so the flow can be exercised end to end
// without the production site.

// Gateway-only-ingress exception (#967/#968): the sign-in flow points the browser at the PUBLIC
// devthrottle.com website (NOT a Director) to mint a device key. This is the one intended
// absolute-URL egress; the Gateway front door is unaffected. The eslint-disable directly below
// carries that reason at the one banned literal so the lint stays green.
// eslint-disable-next-line no-restricted-syntax -- documented Gateway-only-ingress exception (#967/#968)
export const SITE_BASE: string = (import.meta.env.VITE_DT_SITE_BASE as string | undefined) || "https://devthrottle.com";

// The path the site hands the MOBILE shell's device key back to. This stays /m/device-callback even
// though the app itself re-based from /m to /mobile: the devthrottle.com website validates redirect_uri
// against a STRICT allow-list (website/src/lib/loopback.js: MOBILE_CALLBACK_PATH = '/m/device-callback'),
// which lives in a different repo and is not changed here. The Gateway 301-redirects /m/device-callback
// to /mobile/device-callback, and the browser re-attaches the URL fragment (the device key / access
// token) across that redirect, so the callback lands on the app's /mobile/device-callback route. Fully
// canonicalizing this to /mobile/device-callback is a follow-up that also needs the website allow-list
// updated (cross-repo #1081).
export const DEVICE_CALLBACK_PATH = "/m/device-callback";

// The path the site hands the DESKTOP Cockpit browser's device key back to (issue #1088). Must match
// the Cockpit router route and the Gateway's public-path allow-list. NOTE (cross-repo #1081): the
// devthrottle.com activation page must accept this path (today it pins /m/device-callback).
export const COCKPIT_DEVICE_CALLBACK_PATH = "/device-callback";

const STATE_KEY = "cc.enrollState";
const NEXT_KEY = "cc.enrollNext";

/**
 * Everything about the enrollment round trip that differs between the shells sharing this flow. One
 * profile is installed per shell at startup (configureEnrollment); the shared SignIn and
 * DeviceCallback screens read it instead of hardcoding either shell's routes or device identity.
 */
export interface EnrollmentShellProfile {
  /** The path on THIS origin the site hands the device key back to (fragment-only, issue #1082). */
  callbackPath: string;
  /** The platform identifier sent to the site and to the Gateway enroll endpoint. */
  platform: () => string;
  /** The human-recognizable device label shown on the account's Devices page. */
  deviceName: () => string;
  /** The word for this device in the sign-in/callback copy ("phone" only for known phones). */
  deviceLabel: string;
  /** The shell's own in-router sign-in route (the callback's "Try again" target). */
  signInPath: string;
  /** The in-router route to land on after enrollment when no specific route was requested. */
  defaultLanding: string;
  /** The router basename this shell is served under ("/mobile" for mobile, "" for the Cockpit). */
  basename: string;
}

/** The phone platform, as the site's MobileActivate expects it ("android" | "ios"). */
export function detectPlatform(): "android" | "ios" {
  const ua = typeof navigator === "undefined" ? "" : navigator.userAgent || "";
  return /iPhone|iPad|iPod/i.test(ua) ? "ios" : "android";
}

/** A short, non-identifying phone label shown in the account's Devices list. */
export function deviceName(): string {
  return detectPlatform() === "ios" ? "iPhone" : "Android phone";
}

/**
 * The platform identifier a desktop Cockpit browser sends (issue #1088). A non-phone value, so the
 * Gateway records the enrolled device with the "browser" device type instead of "phone".
 * NOTE (cross-repo #1081): the devthrottle.com activation page must accept this value (today it
 * hard-rejects anything but android/ios).
 */
export function desktopPlatform(): string {
  return "browser";
}

/**
 * A human-recognizable desktop-browser label for the account's Devices page (issue #1088), for
 * example "Edge on Windows". Best-effort from the user agent; never identifying beyond
 * browser + operating system.
 */
export function desktopDeviceName(): string {
  const ua = typeof navigator === "undefined" ? "" : navigator.userAgent || "";
  const browser =
    /Edg\//.test(ua) ? "Edge"
    : /OPR\/|Opera/.test(ua) ? "Opera"
    : /Firefox\//.test(ua) ? "Firefox"
    : /Chrome\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari"
    : "Browser";
  const os =
    /Windows/.test(ua) ? "Windows"
    : /Mac OS X|Macintosh/.test(ua) ? "macOS"
    : /Linux/.test(ua) ? "Linux"
    : "desktop";
  return `${browser} on ${os}`;
}

/** The mobile PWA's profile - the default, byte-compatible with the pre-#1088 phone behavior. */
export const MOBILE_ENROLLMENT_PROFILE: EnrollmentShellProfile = {
  callbackPath: DEVICE_CALLBACK_PATH,
  platform: detectPlatform,
  deviceName,
  deviceLabel: "phone",
  signInPath: "/signin",
  defaultLanding: "/",
  basename: "/mobile",
};

/** The desktop Cockpit's profile (issue #1088), installed by apps/cockpit at startup. */
export const COCKPIT_ENROLLMENT_PROFILE: EnrollmentShellProfile = {
  callbackPath: COCKPIT_DEVICE_CALLBACK_PATH,
  platform: desktopPlatform,
  deviceName: desktopDeviceName,
  deviceLabel: "device",
  signInPath: "/signin",
  defaultLanding: "/",
  basename: "",
};

let profile: EnrollmentShellProfile = MOBILE_ENROLLMENT_PROFILE;

/**
 * Install the shell's enrollment profile at startup. The mobile shell may skip this (the mobile
 * profile is the default); the desktop Cockpit installs COCKPIT_ENROLLMENT_PROFILE.
 */
export function configureEnrollment(p: EnrollmentShellProfile): void {
  profile = p;
}

/** The currently-installed shell profile the shared SignIn/DeviceCallback screens read. */
export function enrollmentProfile(): EnrollmentShellProfile {
  return profile;
}

/**
 * Which enrollment credential the website returned in the callback fragment, and therefore which
 * gateway kind this sign-in is for (multi-tenant hosted sign-in, Phase C):
 *   - "hosted": the account's short-lived Supabase access_token, forwarded to the mint as
 *     `Authorization: Bearer` (enrollDeviceHosted).
 *   - "selfHost": a device_key, posted in the request body (enrollDevice), the pre-hosted behavior.
 * null when the fragment carried neither (a malformed or interrupted round trip).
 */
export type EnrollCredential =
  | { mode: "hosted"; accessToken: string }
  | { mode: "selfHost"; deviceKey: string };

/**
 * Decide the enrollment path from the callback fragment. The presence of an access_token means a
 * HOSTED gateway round trip (it is the credential a hosted gateway issues); a device_key means the
 * pre-hosted SELF-HOST round trip. A legitimate callback carries EXACTLY ONE of the two. Both
 * present is AMBIGUOUS - we cannot know which gateway kind the callback is for, and guessing would
 * send the wrong request shape (a hosted Bearer where the Gateway expects a body device_key, or the
 * reverse) - so both-present fails closed to null, exactly like neither-present. Returns null when
 * neither is present, so the callback reports "no device key" exactly as it did before this branch
 * existed.
 */
export function readEnrollCredential(params: URLSearchParams): EnrollCredential | null {
  const accessToken = params.get("access_token");
  const deviceKey = params.get("device_key");
  if (accessToken && deviceKey) return null;
  if (accessToken) return { mode: "hosted", accessToken };
  if (deviceKey) return { mode: "selfHost", deviceKey };
  return null;
}

/** Mint a fresh anti-forgery state, persist it for the return leg, and return it. */
export function newEnrollState(): string {
  const state = crypto.randomUUID();
  try {
    sessionStorage.setItem(STATE_KEY, state);
  } catch {
    /* sessionStorage unavailable - the callback then skips the match (best-effort) */
  }
  return state;
}

/**
 * Read and clear the state saved before the round trip. Returns null when none was saved (private
 * mode, or storage cleared). The callback (runEnrollmentCallback) FAILS CLOSED on a null
 * expected-state: a callback whose round trip cannot be verified against a nonce THIS browser minted
 * is rejected rather than enrolled, which closes the hosted login-CSRF path.
 */
export function takeEnrollState(): string | null {
  try {
    const state = sessionStorage.getItem(STATE_KEY);
    sessionStorage.removeItem(STATE_KEY);
    return state;
  } catch {
    return null;
  }
}

/**
 * An in-app path that is safe to land on after the round trip: root-relative ("/fleet?tab=x") and
 * not protocol-relative ("//evil.example"), so a crafted next= can never send the browser off this
 * origin. Returns null for anything else; the callback then lands on the profile's default.
 */
export function safeInternalPath(raw: string | null | undefined): string | null {
  if (!raw) return null;
  if (!raw.startsWith("/") || raw.startsWith("//")) return null;
  return raw;
}

/**
 * Remember the originally-requested in-app route across the sign-in round trip (issue #1088
 * acceptance: the browser lands back on the exact route it first asked for). Persisted beside the
 * anti-forgery state, because devthrottle.com only ever returns to the fixed callback path. Unsafe
 * or empty values store nothing (the callback then lands on the shell's default).
 */
export function rememberEnrollNext(next: string | null | undefined): void {
  const safe = safeInternalPath(next);
  try {
    if (safe) sessionStorage.setItem(NEXT_KEY, safe);
    else sessionStorage.removeItem(NEXT_KEY);
  } catch {
    /* sessionStorage unavailable - the callback then lands on the default route (best-effort) */
  }
}

/**
 * Read and clear the remembered originally-requested route. Returns null (land on the shell's
 * default) when none was remembered or storage is unavailable; re-validated on the way out so a
 * tampered stored value still cannot leave the origin.
 */
export function takeEnrollNext(): string | null {
  try {
    const next = sessionStorage.getItem(NEXT_KEY);
    sessionStorage.removeItem(NEXT_KEY);
    return safeInternalPath(next);
  } catch {
    return null;
  }
}
