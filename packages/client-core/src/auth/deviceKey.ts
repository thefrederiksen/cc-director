// The shared per-device credential seam (issue #908 for the phone, issue #1088 for the desktop
// Cockpit). The app never receives a master token from the page (the shell carries no secret).
// Instead it holds the per-device key it obtained by signing in on devthrottle.com and enrolling with
// the Gateway (POST /mobile/enroll). That key is sent as the Bearer on every API call. Both shells
// share this one seam: a browser enrolled through either shell is enrolled for the origin.
//
// This file is now a THIN READ over accountStore (devthrottle_internal #1509). It used to own a single
// `cc.deviceKey` string, which is exactly why a browser could only hold one login: a second sign-in
// overwrote the first. The store holds a LIST of accounts and a pointer at the active one, and every
// function here answers about that ACTIVE account. The seam is unchanged on purpose - every API call
// already read through getDeviceKey(), so switching accounts moves the whole app without a single call
// site changing.
import {
  activeAccount,
  listAccounts,
  pendingInstallId,
  removeAccount,
  removeAllAccounts,
} from "./accountStore";

/** The active account's per-device key, or "" when this browser has not enrolled yet. */
export function getDeviceKey(): string {
  return activeAccount()?.deviceKey ?? "";
}

/**
 * Forget the ACTIVE account's key - it was revoked, or the person signed this account out. Any other
 * account on this browser is untouched, and the first of them becomes active, so signing out of one of
 * two logins lands on the other rather than on the sign-in screen.
 */
export function clearDeviceKey(): void {
  const active = activeAccount();
  if (!active) return;
  removeAccount(active.id);
}

/** Forget every account on this browser. */
export function clearAllDeviceKeys(): void {
  removeAllAccounts();
}

/** True once this browser holds at least one enrolled account. */
export function hasDeviceKey(): boolean {
  return getDeviceKey().length > 0;
}

/** True when more than one account is enrolled, which is what makes the shells show the switcher. */
export function hasMultipleAccounts(): boolean {
  return listAccounts().length > 1;
}

/**
 * The install id of the ACTIVE account - its stable device identity, used as `install_id` at
 * devthrottle.com, as `deviceId` at /mobile/enroll, and as the recorder's device id so the server can
 * say which device recorded a clip. Each account carries its OWN, so two logins on one browser are two
 * independent rows on the cloud device roster (see accountStore).
 *
 * Before this browser has enrolled there is no active account, so the id of the enrollment currently in
 * flight is returned instead (accountStore mints one when a sign-in starts). That is the same value the
 * sign-in leg sent to the website, so the enroll leg that follows it matches.
 */
export function getInstallId(): string {
  return activeAccount()?.installId ?? pendingInstallId();
}
