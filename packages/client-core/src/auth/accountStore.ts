// The signed-in ACCOUNTS on this browser (devthrottle_internal #1509). One browser, more than one
// DevThrottle account, each holding its own already-minted per-device key.
//
// This replaces the single `cc.deviceKey` string that used to be the whole credential store. That
// string could only ever describe one login, so signing in as a second account OVERWROTE the first and
// getting back to it meant another full round trip to devthrottle.com. The list here holds every
// account this browser has enrolled, plus a pointer at the active one, so switching costs a pointer
// move and a data refresh - both keys are already minted, so a swap needs no network at all.
//
// EVERY ACCOUNT CARRIES ITS OWN INSTALL ID, and that is not incidental. The install id is sent to
// devthrottle.com as `install_id` and to the Gateway as `deviceId` at enrollment, so it is what the
// cloud device roster keys a device row on. If two accounts shared one install id they would land on
// ONE roster row, and revoking the work phone would silently drop the personal one with it. A per
// account install id makes them two independent devices that happen to live in the same browser.
//
// Storage is localStorage on the Gateway's own origin, exactly where the single key lived.
// `cc.deviceKey` is still written as a MIRROR of the active account's key: it is what a previous app
// bundle reads, so a person left on a cached shell (or a rollback of the deployed bundle) stays signed
// in on their active account instead of being thrown back to the sign-in screen. Nothing in this
// codebase reads that mirror any more - getDeviceKey() reads the active entry - it exists purely so
// the two bundles can coexist during a rollout.

/** One enrolled account on this browser. Never holds an account session - only the local device key. */
export interface StoredAccount {
  /** Stable local identifier for this entry. Never sent anywhere; it only names the entry in this browser. */
  id: string;
  /** What the switcher shows. The account email once known, otherwise "Account 1", "Account 2", ... */
  label: string;
  /** The account email when the Gateway could resolve it, else null. Also the identity accounts dedupe on. */
  email: string | null;
  /** The local per-device key the Gateway issued for THIS account. Sent as the Bearer while active. */
  deviceKey: string;
  /** This account's own install id - its device identity in the cloud roster. See the note above. */
  installId: string;
}

const ACCOUNTS_KEY = "cc.accounts";
const ACTIVE_KEY = "cc.activeAccount";
const PENDING_INSTALL_KEY = "cc.pendingInstallId";
const LEGACY_DEVICE_KEY = "cc.deviceKey";
const LEGACY_INSTALL_KEY = "cc.installId";

// Subscribers re-render when the account list or the active pointer changes. A switch has to move the
// whole app - the roster, the app bar, the recorder - and every one of those reads through here.
type Listener = () => void;
const listeners = new Set<Listener>();

/** Watch for account changes (added, removed, switched). Returns the unsubscribe function. */
export function subscribeToAccounts(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function announce(): void {
  for (const listener of listeners) listener();
}

// ANOTHER TAB CAN CHANGE WHO YOU ARE (devthrottle_internal #1513). localStorage is shared across every
// tab on this origin, but only the tab that ran the switch reloads - so a second tab went on showing
// account A's name and A's roster while its very next call read the shared pointer and authenticated as
// B. That is the work-versus-personal mis-send the hard reload was supposed to make impossible,
// happening in the window the reload does not cover.
//
// The `storage` event fires only in the OTHER tabs, which is exactly the set that needs to know. A
// change to the active pointer is an identity change and the tab reloads; a change to the list alone
// (an account added or renamed elsewhere) only re-renders, because the identity still holds.
if (typeof window !== "undefined") {
  window.addEventListener("storage", (event) => {
    if (event.key === null) {
      // The whole origin's storage was cleared out from under us - there is no identity left to trust.
      window.location.reload();
      return;
    }
    if (event.key === ACTIVE_KEY) {
      window.location.reload();
      return;
    }
    if (event.key === ACCOUNTS_KEY) announce();
  });
}

function readRaw(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    // Storage unavailable (private mode). The app behaves as a browser that has never enrolled, which
    // is what it did before this store existed.
    return null;
  }
}

function writeRaw(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* storage unavailable (private mode) - the app will simply prompt to sign in again */
  }
}

function deleteRaw(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    /* ignore */
  }
}

/**
 * Every field of a stored entry must be a usable string, or the entry is not an account. A partially
 * written entry is dropped rather than repaired: a blank device key would authenticate nothing, and a
 * blank install id would enroll as a nameless device.
 */
function isAccount(value: unknown): value is StoredAccount {
  if (typeof value !== "object" || value === null) return false;
  const a = value as Partial<StoredAccount>;
  return (
    typeof a.id === "string" && a.id.length > 0 &&
    typeof a.label === "string" &&
    typeof a.deviceKey === "string" && a.deviceKey.length > 0 &&
    typeof a.installId === "string" && a.installId.length > 0
  );
}

function parseAccounts(raw: string | null): StoredAccount[] {
  if (!raw) return [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    // The stored list is not JSON. Treat it as no accounts rather than crashing the shell on boot -
    // the person signs in again, which rewrites it.
    return [];
  }
  if (!Array.isArray(parsed)) return [];
  return parsed.filter(isAccount).map((a) => ({
    id: a.id,
    label: a.label,
    email: typeof a.email === "string" && a.email.length > 0 ? a.email : null,
    deviceKey: a.deviceKey,
    installId: a.installId,
  }));
}

function persist(accounts: StoredAccount[], activeId: string): void {
  writeRaw(ACCOUNTS_KEY, JSON.stringify(accounts));
  if (activeId) writeRaw(ACTIVE_KEY, activeId);
  else deleteRaw(ACTIVE_KEY);

  // Keep the previous bundle's single-key store pointing at whichever account is active. See the note at
  // the top of this file: this is a rollout mirror, not a credential this code reads.
  //
  // THE KEY AND THE INSTALL ID MOVE TOGETHER. Mirroring only the key left the pair MISMATCHED after a
  // switch - the old bundle would read account B's device key beside account A's install id, and enroll
  // or file a recording as if B were A's device row. They are one identity; a mirror that copies half of
  // it is worse than no mirror, because the halves are individually plausible.
  const active = accounts.find((a) => a.id === activeId);
  if (active) {
    writeRaw(LEGACY_DEVICE_KEY, active.deviceKey);
    writeRaw(LEGACY_INSTALL_KEY, active.installId);
  } else {
    deleteRaw(LEGACY_DEVICE_KEY);
    deleteRaw(LEGACY_INSTALL_KEY);
  }
}

/**
 * Adopt a pre-#1509 single-key enrollment as the first account in the list, so upgrading the bundle
 * never signs anybody out. Runs once: the moment the list is written the legacy key stops being read.
 * Returns the migrated list, or an empty list when there was nothing to migrate.
 */
function migrateLegacy(): StoredAccount[] {
  const legacyKey = readRaw(LEGACY_DEVICE_KEY);
  if (!legacyKey) return [];

  const migrated: StoredAccount = {
    id: newId(),
    label: "Account 1",
    email: null,
    deviceKey: legacyKey,
    // Reuse the browser's existing install id so the migrated account keeps the SAME cloud roster row
    // it already had. Minting a fresh one here would strand the person's existing device registration
    // and show them a duplicate device on the website.
    installId: readRaw(LEGACY_INSTALL_KEY) || newId(),
  };
  persist([migrated], migrated.id);
  writeRaw(LEGACY_INSTALL_KEY, migrated.installId);
  return [migrated];
}

function newId(): string {
  return crypto.randomUUID();
}

/** Where an unreadable account list is kept instead of being overwritten. See listAccounts. */
const DAMAGED_KEY = "cc.accounts.damaged";

/**
 * Every account enrolled on this browser, in the order they were added.
 *
 * MIGRATION RUNS ONLY WHEN THERE IS NO LIST AT ALL. An earlier version keyed that decision on the list
 * being EMPTY, which is the same thing right up until the list is damaged: a `cc.accounts` value that
 * would not parse read as zero accounts, migration then adopted the legacy single key, and persisting
 * that one account OVERWROTE the damaged value - permanently destroying every inactive account's key,
 * including ones that might still have been recoverable by hand. An absent list and an unreadable list
 * are different states and must not share a code path.
 *
 * A damaged value is moved aside rather than deleted, so nothing this code could not read is thrown
 * away by this code. The person signs in again, which writes a clean list.
 */
export function listAccounts(): StoredAccount[] {
  const raw = readRaw(ACCOUNTS_KEY);
  const stored = parseAccounts(raw);
  if (stored.length > 0) return stored;

  if (raw) {
    // A list EXISTS and yielded nothing usable. Do not migrate over it, and do not delete it.
    if (readRaw(DAMAGED_KEY) === null) writeRaw(DAMAGED_KEY, raw);
    deleteRaw(ACCOUNTS_KEY);
    deleteRaw(ACTIVE_KEY);
    return [];
  }

  return migrateLegacy();
}

/**
 * The account whose device key the app is currently authenticating with, or null when this browser has
 * not enrolled. When the stored pointer names an entry that no longer exists the FIRST account is
 * active instead - a dangling pointer must not present as signed out while a usable key is sitting in
 * the list.
 */
export function activeAccount(): StoredAccount | null {
  const accounts = listAccounts();
  if (accounts.length === 0) return null;
  const id = readRaw(ACTIVE_KEY);
  return accounts.find((a) => a.id === id) ?? accounts[0];
}

/**
 * Make an account the active one. Returns false when no such account is stored, so a caller driving
 * this from a stale list can tell nothing happened rather than assuming it switched.
 */
export function setActiveAccount(id: string): boolean {
  const accounts = listAccounts();
  if (!accounts.some((a) => a.id === id)) return false;
  persist(accounts, id);
  announce();
  return true;
}

/**
 * Which stored entry a fresh enrollment REPLACES, or undefined to append a new one.
 *
 * Two rules, and the second is the one that was missing:
 *
 *  1. Same email, compared case-insensitively. Addresses are not case-sensitive in practice and the
 *     Gateway is not guaranteed to hand back the same casing twice, so an exact match would let
 *     "Soren@..." and "soren@..." become two rows for one account.
 *  2. THE MIGRATED ACCOUNT. Migration deliberately stores email: null, because a browser upgrading from
 *     the single-key store has no identity to record. Matching on email alone therefore never
 *     recognised it, so the very first person to re-sign-in on the account they were already using got
 *     a SECOND entry and a second device row - which is the one upgrade path everybody takes. An
 *     identity-less entry is adopted when it is unambiguous: exactly one exists, and nothing matched by
 *     email. Two of them is ambiguous and appends instead, because guessing which is worse than a
 *     duplicate the Accounts screen can remove.
 */
function findExisting(accounts: StoredAccount[], email: string | null): StoredAccount | undefined {
  // No idea who just signed in, so no basis to claim they are anyone already here. Append. Collapsing
  // two unidentified enrollments into one entry would DESTROY a device key - the case that matters is a
  // self-host Gateway, where identity is often unresolvable and two accounts would silently become one.
  if (!email) return undefined;

  const wanted = email.toLowerCase();
  const byEmail = accounts.find((a) => a.email !== null && a.email.toLowerCase() === wanted);
  if (byEmail) return byEmail;

  // Nothing matched by email, and exactly one stored account has no identity at all. That is the
  // migrated entry - it is the one shape that arrives without an email, because a browser upgrading
  // from the single-key store had nothing to record - and this sign-in has just told us who it is. Two
  // identity-less entries is ambiguous, so it appends: a duplicate the Accounts screen can remove beats
  // a guess that overwrites the wrong key.
  const identityLess = accounts.filter((a) => a.email === null);
  return identityLess.length === 1 ? identityLess[0] : undefined;
}

/**
 * Record a freshly-enrolled account and make it active.
 *
 * An account already stored for the same identity is REPLACED rather than appended: signing in again on
 * an account you already hold (after a revoke, or just to refresh it) means one entry with a new key,
 * never a second identical row in the switcher. See findExisting for what counts as the same identity.
 */
export function addAccount(entry: { deviceKey: string; installId: string; email: string | null; label?: string }): StoredAccount {
  const accounts = listAccounts();
  const existing = findExisting(accounts, entry.email);

  const account: StoredAccount = {
    id: existing?.id ?? newId(),
    label: entry.label ?? entry.email ?? existing?.label ?? `Account ${accounts.length + 1}`,
    email: entry.email,
    deviceKey: entry.deviceKey,
    installId: entry.installId,
  };

  const next = existing
    ? accounts.map((a) => (a.id === existing.id ? account : a))
    : [...accounts, account];

  persist(next, account.id);
  announce();
  return account;
}

/**
 * Attach an identity that arrived AFTER the account was stored (devthrottle_internal #1513).
 *
 * Enrollment writes the key immediately and looks up who it belongs to afterwards, so the account
 * exists for a moment with no email. This fills that in. The label is only overwritten while it is
 * still the positional placeholder - a name the person chose themselves is theirs to keep.
 */
export function nameAccount(id: string, email: string): void {
  const accounts = listAccounts();
  const next = accounts.map((a) =>
    a.id === id
      ? { ...a, email, label: /^Account \d+$/.test(a.label) ? email : a.label }
      : a,
  );
  persist(next, activeAccount()?.id ?? "");
  announce();
}

/** Give an account a name of its own in the switcher ("Work", "Personal"). */
export function renameAccount(id: string, label: string): void {
  const accounts = listAccounts();
  const trimmed = label.trim();
  if (trimmed.length === 0) return;
  const next = accounts.map((a) => (a.id === id ? { ...a, label: trimmed } : a));
  persist(next, activeAccount()?.id ?? "");
  announce();
}

/**
 * Sign one account out of this browser. Its device key is forgotten here; the device stays on that
 * account's roster at devthrottle.com until it is removed there.
 *
 * Removing the ACTIVE account activates the first one that remains, so a person with two logins who
 * signs out of one lands on the other rather than on the sign-in screen.
 */
export function removeAccount(id: string): void {
  const accounts = listAccounts();
  const next = accounts.filter((a) => a.id !== id);
  const wasActive = activeAccount()?.id === id;
  const activeId = wasActive ? (next[0]?.id ?? "") : (activeAccount()?.id ?? "");
  persist(next, activeId);
  announce();
}

/** Sign every account out of this browser. */
export function removeAllAccounts(): void {
  persist([], "");
  announce();
}

/**
 * Mint the install id for an enrollment that is ABOUT to start, and persist it so the callback leg can
 * pick it up after the round trip through devthrottle.com.
 *
 * A fresh id per sign-in is what makes "add account" produce a second device rather than colliding
 * with the account already on this browser. SignIn calls this on the way out; the callback consumes it
 * with takePendingInstallId() on the way back.
 */
export function newPendingInstallId(): string {
  const id = newId();
  writeRaw(PENDING_INSTALL_KEY, id);
  return id;
}

/**
 * The install id minted for the enrollment currently in flight. Mints one when none was saved (storage
 * cleared mid-flight, or a callback reached directly), so the enroll request always carries a device
 * id - an enrollment with no device id is not something to attempt.
 */
export function pendingInstallId(): string {
  const saved = readRaw(PENDING_INSTALL_KEY);
  if (saved) return saved;
  return newPendingInstallId();
}

/** Consume the in-flight install id once the account it belongs to has been stored. */
export function clearPendingInstallId(): void {
  deleteRaw(PENDING_INSTALL_KEY);
}
