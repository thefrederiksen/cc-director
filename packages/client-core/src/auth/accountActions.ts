// Switching and signing out (devthrottle_internal #1507, #1509). accountStore owns the STORAGE; this
// owns what the running app has to do about a change of identity, which is more than moving a pointer.
//
// Both actions finish with a HARD navigation rather than a router push, and that is deliberate. The key
// is not read once at startup - it is mirrored into the cc-gateway-token cookie, held open by live
// terminal WebSockets, and baked into every list the shells have already fetched. Re-rendering would
// leave the previous account's sessions on screen next to the new account's name, which is the one
// mistake that actually costs something: a message typed into what looks like your work fleet and sent
// to your personal one. Reloading guarantees every one of those is rebuilt from the new credential.
import { clearGatewayCookie, ensureGatewayCookie } from "../api/client";
import { removeAccount, removeAllAccounts, setActiveAccount } from "./accountStore";
import { getDeviceKey } from "./deviceKey";
import { enrollmentProfile } from "./enrollRequest";

/** Where a shell lands after an identity change - its own app root, under its own router basename. */
function appRoot(): string {
  const profile = enrollmentProfile();
  return `${profile.basename}${profile.defaultLanding}`;
}

/** Where a shell sends a browser that now holds no account at all. */
function signInRoot(): string {
  const profile = enrollmentProfile();
  return `${profile.basename}${profile.signInPath}`;
}

/**
 * Make another enrolled account the active one and reload the app as them. No network call and no
 * sign-in: that account's device key was already minted, so this works offline.
 *
 * Does nothing when the id names no stored account, so a switcher rendered from a stale list cannot
 * silently reload the app as the account it was already showing.
 */
export function switchAccount(id: string): void {
  if (!setActiveAccount(id)) return;
  ensureGatewayCookie();
  if (typeof window === "undefined") return;
  window.location.assign(appRoot());
}

/**
 * Sign ONE account out of this browser and reload.
 *
 * Only this browser forgets the key; the device stays on that account's roster at devthrottle.com until
 * it is removed there. When another account remains it becomes active and the app reloads as them;
 * when that was the last one the browser lands on the sign-in screen with the mirrored cookie dropped.
 */
export function signOutAccount(id: string): void {
  removeAccount(id);
  if (getDeviceKey()) {
    ensureGatewayCookie();
    if (typeof window !== "undefined") window.location.assign(appRoot());
    return;
  }
  clearGatewayCookie();
  if (typeof window !== "undefined") window.location.assign(signInRoot());
}

/** Sign every account out of this browser and land on the sign-in screen. */
export function signOutAllAccounts(): void {
  removeAllAccounts();
  clearGatewayCookie();
  if (typeof window === "undefined") return;
  window.location.assign(signInRoot());
}
