// Shared helpers for building the device-enrollment request the app sends to devthrottle.com and for
// verifying the round trip when it returns (issue #908).
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

// The path the site hands the device key back to. Must match the mobile router route and the site's
// MOBILE_CALLBACK_PATH (website/src/lib/loopback.js).
export const DEVICE_CALLBACK_PATH = "/m/device-callback";

const STATE_KEY = "cc.enrollState";

/** The phone platform, as the site's MobileActivate expects it ("android" | "ios"). */
export function detectPlatform(): "android" | "ios" {
  const ua = typeof navigator === "undefined" ? "" : navigator.userAgent || "";
  return /iPhone|iPad|iPod/i.test(ua) ? "ios" : "android";
}

/** A short, non-identifying device label shown in the account's Devices list. */
export function deviceName(): string {
  return detectPlatform() === "ios" ? "iPhone" : "Android phone";
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
 * mode, or storage cleared); the callback treats a null expected-state as "cannot verify, proceed"
 * rather than blocking a legitimate sign-in.
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
