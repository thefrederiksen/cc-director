// Switching and signing out (devthrottle_internal #1507, #1509, #1513). accountStore owns the STORAGE;
// this owns what the running app and the SERVER have to do about a change of identity, which is more
// than moving a pointer.
//
// TWO THINGS CARRY THE CREDENTIAL, and both have to move together:
//
//   - The Bearer, read from local storage on every API call. Moving it is a pointer write.
//   - The cc-gateway-token COOKIE, which authenticates the live terminal WebSocket and every bare
//     <img>/<iframe> source, because those cannot carry a header. It is HttpOnly, so this side cannot
//     touch it at all - the SERVER moves it (POST/DELETE /account/device-cookie).
//
// Miss the second and nothing looks wrong: every API call is correct, while the terminal stream and the
// file viewer keep serving the account you just left. That is what #1509 shipped, and it is fixed here.
//
// Both actions finish with a HARD navigation rather than a router push. The key is not read once at
// startup - it is held open by live sockets and baked into every list already fetched. Re-rendering
// would leave one account's sessions on screen under another account's name, which is how a message
// meant for the work fleet reaches the personal one.
import { adoptGatewayCookie, clearGatewayCookie } from "../api/client";
import { releasePushForCurrentAccount } from "../push/register";
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
 * Make another enrolled account the active one and reload the app as them. No sign-in and no password:
 * that account's device key was already minted.
 *
 * The one network call is the cookie hand-over, and it is AWAITED - reloading first would start the new
 * page while the cookie still named the old account, so its terminal socket would open as the wrong
 * identity. Offline-tolerant: a failed hand-over is completed by the next load, which calls it again.
 *
 * Does nothing when the id names no stored account, so a switcher rendered from a stale list cannot
 * silently reload the app as the account it was already showing.
 */
export async function switchAccount(id: string): Promise<void> {
  // BEFORE the pointer moves: hand this browser's push subscription back from the OUTGOING account, so
  // the phone stops receiving that account's notifications while showing the other one. It authenticates
  // as the account being left, which is why it cannot wait until after the switch.
  await releasePushForCurrentAccount();
  if (!setActiveAccount(id)) return;
  await adoptGatewayCookie();
  if (typeof window === "undefined") return;
  window.location.assign(appRoot());
}

/** What a sign-out did, so the screen can tell the truth about a server that would not co-operate. */
export type SignOutResult = { ok: true } | { ok: false; reason: string };

const UNREACHABLE =
  "The Gateway could not be reached, so this device is still signed in. Nothing was changed - " +
  "check your connection and try again.";

/**
 * Sign ONE account out of this browser.
 *
 * ORDER IS LOAD-BEARING. The cookie is cleared FIRST, while the key is still held, because that request
 * has to authenticate to be accepted - forget the key locally first and the cookie can never be cleared
 * from this browser again.
 *
 * IF THE SERVER CLEAR FAILS, NOTHING IS SIGNED OUT. Removing the key locally while the cookie stayed
 * live would produce exactly the lie this work exists to remove: a screen saying signed out, beside a
 * credential that still opens terminal sockets and loads files for thirty days. The caller is told why
 * and can retry. Half a sign-out is not a sign-out.
 *
 * Only this browser forgets the key either way - the device stays on that account's roster at
 * devthrottle.com until it is removed there.
 */
export async function signOutAccount(id: string): Promise<SignOutResult> {
  if (!(await clearGatewayCookie())) return { ok: false, reason: UNREACHABLE };

  // Done while the key is still held, for the same reason the cookie is: it authenticates as the account
  // being signed out. A device that keeps its push registration goes on buzzing for an account it can no
  // longer open.
  await releasePushForCurrentAccount();
  removeAccount(id);

  // Another account remains and is now active, so the cookie is handed to IT rather than left cleared -
  // otherwise the app reloads as that account with its sockets unauthenticated.
  if (getDeviceKey()) {
    await adoptGatewayCookie();
    if (typeof window !== "undefined") window.location.assign(appRoot());
    return { ok: true };
  }

  if (typeof window !== "undefined") window.location.assign(signInRoot());
  return { ok: true };
}

/**
 * Sign every account out of this browser and land on the sign-in screen. Same ordering and same refusal
 * as signOutAccount: the cookie goes first, and a server that will not clear it means nothing is signed
 * out at all.
 */
export async function signOutAllAccounts(): Promise<SignOutResult> {
  if (!(await clearGatewayCookie())) return { ok: false, reason: UNREACHABLE };

  await releasePushForCurrentAccount();
  removeAllAccounts();
  if (typeof window !== "undefined") window.location.assign(signInRoot());
  return { ok: true };
}
