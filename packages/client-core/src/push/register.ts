// Web Push subscription + app-icon badge control, shared by BOTH client shells (the mobile PWA and
// the desktop Cockpit). One copy of the subscribe/unsubscribe contract so the two shells never drift
// (issue #1257: the Cockpit reuses the exact plumbing the phone shipped with in #905).
//
// The flow: the user turns notifications on (a user gesture - a button tap or a settings checkbox,
// required by the browser to prompt), we ask for Notification permission, subscribe the browser's
// push manager against the Gateway's VAPID public key, and register that subscription with the
// Gateway. From then on the Gateway pushes the current "needs you" count while the app is closed, and
// each shell's own service worker draws the notification (mobile: public/push-sw.js -> the app-icon
// dot; Cockpit: public/sw.js -> a desktop notification). While the app is OPEN, the roster keeps the
// state in sync via reconcileBadge() and clears it when nothing is waiting - the Gateway never pushes
// a zero except the single falling-edge clear, so foreground clearing is the client's job.
//
// This module is shell-neutral: it only talks to the root-relative /push/* endpoints and to
// navigator.serviceWorker.ready (whichever service worker the shell registered). The shell-specific
// bits - the notification icon and the click-target URL - live in each shell's service worker, never
// here.

import { authHeaders } from "../api/client";

// Kept in sync with the tag public/push-sw.js uses, so the page can close the same notification.
const NEEDS_YOU_TAG = "devthrottle-needs-you";

// navigator.setAppBadge / clearAppBadge are not in every TypeScript DOM lib yet; declare the narrow
// shape we use. Absent on Android (feature-detected before every call).
interface BadgeNavigator {
  setAppBadge?: (count?: number) => Promise<void>;
  clearAppBadge?: () => Promise<void>;
}

/** True when this browser can do Web Push at all (installed-PWA secure context with a service worker). */
export function pushSupported(): boolean {
  return (
    typeof navigator !== "undefined" &&
    "serviceWorker" in navigator &&
    "PushManager" in window &&
    "Notification" in window
  );
}

/** The current Notification permission, or "unsupported" when the browser has no Notification API. */
export function notificationPermission(): NotificationPermission | "unsupported" {
  if (typeof Notification === "undefined") return "unsupported";
  return Notification.permission;
}

async function getVapidPublicKey(): Promise<string> {
  const res = await fetch("/push/vapid-public-key", {
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) throw new Error(`GET /push/vapid-public-key failed: ${res.status}`);
  const body = (await res.json()) as { publicKey?: string };
  const key = (body.publicKey ?? "").trim();
  if (key.length === 0) throw new Error("Gateway returned an empty VAPID public key");
  return key;
}

// The applicationServerKey must be the raw bytes of the base64url VAPID public key.
export function urlBase64ToUint8Array(base64: string): Uint8Array {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const normalized = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(normalized);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

async function postSubscription(subscription: PushSubscription): Promise<void> {
  const res = await fetch("/push/subscribe", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify(subscription.toJSON()),
  });
  if (!res.ok) throw new Error(`POST /push/subscribe failed: ${res.status}`);
}

async function postUnsubscribe(endpoint: string): Promise<void> {
  const res = await fetch("/push/unsubscribe", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ endpoint }),
  });
  if (!res.ok) throw new Error(`POST /push/unsubscribe failed: ${res.status}`);
}

/**
 * How long to wait for a service worker to take control of this page before declaring that there is
 * none. This is NOT a fallback - nothing degrades on expiry, the attempt FAILS with a stated reason.
 * It exists because navigator.serviceWorker.ready never resolves AND never rejects when no worker is
 * registered for this scope, so awaiting it bare is an infinite hang: the button that awaited it sat
 * on "Enabling..." for ever and the user read that as "nothing happens".
 */
const SERVICE_WORKER_READY_TIMEOUT_MS = 8000;

// Wait, with a bound, for the service worker that draws the notification. Throws with a reason on
// expiry instead of hanging (see SERVICE_WORKER_READY_TIMEOUT_MS).
async function serviceWorkerReady(): Promise<ServiceWorkerRegistration> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expiry = new Promise<never>((_resolve, reject) => {
    timer = setTimeout(
      () =>
        reject(
          new Error(
            "no service worker is running for this page, so there is nothing to draw the notification",
          ),
        ),
      SERVICE_WORKER_READY_TIMEOUT_MS,
    );
  });
  try {
    return await Promise.race([navigator.serviceWorker.ready, expiry]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

// Ensure the browser has a push subscription and the Gateway knows about it. Idempotent: reuses an
// existing subscription and re-registers it (a cheap upsert on the Gateway).
async function subscribeAndRegister(): Promise<void> {
  const registration = await serviceWorkerReady();
  let subscription = await registration.pushManager.getSubscription();
  if (subscription === null) {
    const key = await getVapidPublicKey();
    subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(key),
    });
  }
  await postSubscription(subscription);
}

/**
 * What happened when the user asked for notifications. EVERY path has a name, because the old
 * "granted | denied | unsupported" triple could not tell the two silent failures apart from a
 * decision the user made:
 *
 *  - "granted"     - permission given AND the subscription is registered with the Gateway. Done.
 *  - "blocked"     - the browser holds a standing block for this site. The user must lift it in the
 *                    browser's own settings; asking again cannot help.
 *  - "dismissed"   - the prompt was dismissed, or the browser never showed one (Chrome's quiet
 *                    permission user interface, or an in-app browser window that is not allowed to
 *                    ask). Permission is still undecided, so asking again CAN help. This is the case
 *                    that used to look exactly like "nothing happened": permission was unchanged, so
 *                    the banner re-rendered identically and said nothing.
 *  - "failed"      - permission WAS given but the subscription could not be created or registered
 *                    (no service worker, no key from the Gateway, a rejected registration). This one
 *                    used to be the worse silence: the banner disappeared as if it had worked, while
 *                    no push would ever arrive.
 *  - "unsupported" - this browser cannot do Web Push at all.
 */
export type EnablePushState = "granted" | "blocked" | "dismissed" | "failed" | "unsupported";

/**
 * The outcome plus a plain-English sentence to show the user. The message lives HERE, next to the
 * decision that produced it, so both shells say the same true thing about the same outcome instead of
 * each inventing wording (and, as the mobile banner did, inventing nothing at all).
 */
export interface EnablePushResult {
  state: EnablePushState;
  /** Ready to render as-is. Never empty - there is no outcome we have nothing to say about. */
  message: string;
}

/**
 * Prompt for permission (must be called from a user gesture) and subscribe. NEVER throws and never
 * returns silently: every path comes back as a named state with a sentence the caller must show.
 */
export async function enablePush(): Promise<EnablePushResult> {
  if (!pushSupported()) {
    return {
      state: "unsupported",
      message: "This browser cannot show notifications.",
    };
  }

  let permission: NotificationPermission;
  try {
    permission = await Notification.requestPermission();
  } catch (err) {
    // Some browsers reject rather than resolve when asking is not allowed here at all.
    return {
      state: "failed",
      message: `This browser refused to ask for notification permission: ${errorText(err)}`,
    };
  }

  if (permission === "denied") {
    return {
      state: "blocked",
      message:
        "Notifications are blocked for DevThrottle. Allow them in your browser's site settings for this site, then tap Enable notifications again.",
    };
  }

  if (permission !== "granted") {
    // Still "default": either the user dismissed the prompt, or no prompt was ever shown. Name the
    // one cause that is invisible to the user and that they can actually act on - a window that is
    // not the installed app cannot be granted notification permission on Android.
    return {
      state: "dismissed",
      message: installedAsApp()
        ? "The permission prompt was dismissed, so notifications are still off. Tap Enable notifications to be asked again."
        : "Your browser did not ask for permission. This window is not the installed DevThrottle app - open DevThrottle from its icon on your home screen and turn notifications on there.",
    };
  }

  try {
    await subscribeAndRegister();
  } catch (err) {
    return {
      state: "failed",
      message: `Notifications are allowed, but this device could not be registered for them: ${errorText(err)}`,
    };
  }

  return {
    state: "granted",
    message: "Notifications are on for this device.",
  };
}

/**
 * True when the page is running as the INSTALLED app (an installed Progressive Web App, or a desktop
 * window) rather than inside browser chrome. Read only to explain a missing permission prompt: on
 * Android an in-app browser window - the one with a close button and the address shown above the page,
 * which is what a tapped link opens - is not allowed to ask, and a user staring at a button that
 * appears to do nothing has no way to know that.
 */
export function installedAsApp(): boolean {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return false;
  return ["standalone", "fullscreen", "minimal-ui", "window-controls-overlay"].some(
    (mode) => window.matchMedia(`(display-mode: ${mode})`).matches,
  );
}

function errorText(err: unknown): string {
  if (err instanceof Error && err.message.trim().length > 0) return err.message;
  return String(err);
}

/**
 * Turn notifications OFF for THIS browser only: drop the browser's push subscription and tell the
 * Gateway to forget it, so the notifier stops pushing to this device. Per-device by construction -
 * a phone or another browser stays subscribed and keeps getting the "needs you" push (issue #1257).
 * A no-op when this browser has no subscription. Throws on a failed Gateway call so the caller can
 * surface the real reason (no silent success).
 */
export async function disablePush(): Promise<void> {
  if (!("serviceWorker" in navigator)) return;
  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.getSubscription();
  if (subscription === null) return;
  const { endpoint } = subscription;
  await subscription.unsubscribe();
  await postUnsubscribe(endpoint);
}

/**
 * Hand the browser's push subscription BACK from the account that is active right now
 * (devthrottle_internal #1513), without unsubscribing the browser itself.
 *
 * A browser has exactly ONE push subscription for the origin, but the Gateway stores it per account.
 * Startup registers whatever subscription exists under whichever account is active, so with two
 * accounts the SAME endpoint ends up registered under both: the phone then receives pushes from an
 * account the person is not looking at, those notifications overwrite each other under a shared tag,
 * and tapping one opens whichever account happens to be active rather than the one that sent it.
 *
 * MUST be called BEFORE the active account changes - it authenticates as the outgoing account, which
 * is the registration being released. The browser subscription is deliberately kept: the next load
 * re-registers it under the incoming account (ensurePushSubscribed), so notifications follow the
 * person rather than accumulating.
 *
 * Never throws. Losing a push registration must not be able to block a switch or a sign-out.
 */
export async function releasePushForCurrentAccount(): Promise<void> {
  try {
    if (!("serviceWorker" in navigator)) return;
    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.getSubscription();
    if (subscription === null) return;
    await postUnsubscribe(subscription.endpoint);
  } catch (err) {
    console.warn("[push] releasing the subscription from the outgoing account failed:", err);
  }
}

/**
 * Whether THIS browser currently has a live push subscription (permission granted AND a push manager
 * subscription present). Used to render the notifications toggle in its true state on load, so the
 * checkbox reflects reality rather than a guess. Returns false when push is unsupported.
 */
export async function isPushSubscribed(): Promise<boolean> {
  if (!pushSupported()) return false;
  if (Notification.permission !== "granted") return false;
  const registration = await navigator.serviceWorker.ready;
  return (await registration.pushManager.getSubscription()) !== null;
}

/**
 * On app start, silently re-register the push subscription IF the user already granted permission
 * before. Never prompts (that needs a gesture) - it just keeps the Gateway's record fresh across
 * subscription rotations. A failure is non-fatal (logged, not thrown): the app works without push.
 */
export async function ensurePushSubscribed(): Promise<void> {
  if (!pushSupported()) return;
  if (Notification.permission !== "granted") return;
  try {
    await subscribeAndRegister();
  } catch (err) {
    console.warn("[push] silent re-subscribe failed:", err);
  }
}

/**
 * Keep the app-icon badge in sync with the live roster while the app is open. count > 0 sets the
 * numeric badge (iOS/desktop); count <= 0 clears the badge AND closes the service worker's dot
 * notification, so returning to the app with nothing waiting removes the dot on every platform.
 */
export async function reconcileBadge(count: number): Promise<void> {
  const nav = navigator as Navigator & BadgeNavigator;
  if (count > 0) {
    if (typeof nav.setAppBadge === "function") {
      await nav.setAppBadge(count).catch(() => undefined);
    }
    return;
  }
  await clearNeedsYouDot();
}

// Clear the badge and close the SW's "needs you" notification (which is the Android dot).
async function clearNeedsYouDot(): Promise<void> {
  const nav = navigator as Navigator & BadgeNavigator;
  if (typeof nav.clearAppBadge === "function") {
    await nav.clearAppBadge().catch(() => undefined);
  }
  if (!("serviceWorker" in navigator)) return;
  try {
    const registration = await navigator.serviceWorker.ready;
    const notifications = await registration.getNotifications({ tag: NEEDS_YOU_TAG });
    notifications.forEach((n) => n.close());
    // Belt and suspenders: also ask the SW to clear, covering any platform quirk in getNotifications.
    registration.active?.postMessage({ type: "devthrottle-clear-needs-you" });
  } catch (err) {
    console.warn("[push] clear dot failed:", err);
  }
}
