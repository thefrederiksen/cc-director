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

// Ensure the browser has a push subscription and the Gateway knows about it. Idempotent: reuses an
// existing subscription and re-registers it (a cheap upsert on the Gateway).
async function subscribeAndRegister(): Promise<void> {
  const registration = await navigator.serviceWorker.ready;
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
 * Prompt for permission (must be called from a user gesture) and subscribe. Returns the outcome so
 * the caller can update the UI: "granted" (subscribed), "denied" (user said no), or "unsupported".
 */
export async function enablePush(): Promise<"granted" | "denied" | "unsupported"> {
  if (!pushSupported()) return "unsupported";
  const permission = await Notification.requestPermission();
  if (permission !== "granted") return "denied";
  await subscribeAndRegister();
  return "granted";
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
