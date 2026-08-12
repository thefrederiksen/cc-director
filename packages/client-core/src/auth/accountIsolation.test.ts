// Account ISOLATION (devthrottle_internal #1512, #1513) - the review findings that a text assertion
// could never have caught, because in every one of them the screen looks right.
//
// The shape they share: local storage, the IndexedDB queues and the cc-gateway-token cookie are all
// scoped to the ORIGIN, while the identity is a pointer inside them. Anything that reads the pointer
// LATER than it was written reads whoever is active then, not whoever it was written for.
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  activeAccount,
  addAccount,
  listAccounts,
  removeAccount,
  setActiveAccount,
} from "./accountStore";

function installStorage(seed: Record<string, string> = {}): Map<string, string> {
  const map = new Map<string, string>(Object.entries(seed));
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
    removeItem: (k: string) => void map.delete(k),
    clear: () => map.clear(),
    key: (i: number) => [...map.keys()][i] ?? null,
    get length() {
      return map.size;
    },
  });
  return map;
}

let uuidCounter = 0;
beforeEach(() => {
  uuidCounter = 0;
  vi.stubGlobal("crypto", { randomUUID: () => `id-${++uuidCounter}` });
  installStorage();
});

describe("the legacy mirror is a matched PAIR", () => {
  it("moves the install id with the key on every switch, so an old bundle cannot pair A's id with B's key", () => {
    const map = installStorage();
    const a = addAccount({ deviceKey: "key-a", installId: "install-a", email: "a@example.com" });
    addAccount({ deviceKey: "key-b", installId: "install-b", email: "b@example.com" });

    // B is active: both halves must describe B.
    expect(map.get("cc.deviceKey")).toBe("key-b");
    expect(map.get("cc.installId")).toBe("install-b");

    setActiveAccount(a.id);

    // And both halves must have moved together. Mirroring only the key left a cached bundle enrolling
    // or filing recordings as if B were A's device row - each half individually plausible.
    expect(map.get("cc.deviceKey")).toBe("key-a");
    expect(map.get("cc.installId")).toBe("install-a");
  });

  it("takes both halves away when the last account goes", () => {
    const map = installStorage();
    const only = addAccount({ deviceKey: "key-a", installId: "install-a", email: "a@example.com" });

    removeAccount(only.id);

    expect(map.get("cc.deviceKey")).toBeUndefined();
    expect(map.get("cc.installId")).toBeUndefined();
  });
});

describe("a damaged account list is never overwritten", () => {
  it("does NOT migrate over it, which would have destroyed every inactive account's key", () => {
    // The real shape of the bug: a populated multi-account store whose list has been corrupted, beside
    // the legacy mirror that names only the ACTIVE account. Migrating here would rewrite the list as
    // that one account and drop the others permanently.
    const map = installStorage({
      "cc.accounts": "{{{ not json",
      "cc.deviceKey": "key-of-the-active-one",
      "cc.installId": "install-of-the-active-one",
    });

    const accounts = listAccounts();

    expect(accounts).toEqual([]);
    // The unreadable value is kept, not deleted - this code does not throw away what it could not read.
    expect(map.get("cc.accounts.damaged")).toBe("{{{ not json");
  });

  it("still migrates a genuinely ABSENT list, which is a different state entirely", () => {
    installStorage({ "cc.deviceKey": "old-key", "cc.installId": "old-install" });

    const accounts = listAccounts();

    expect(accounts).toHaveLength(1);
    expect(accounts[0].deviceKey).toBe("old-key");
  });
});

describe("recognising the migrated account", () => {
  it("adopts the identity-less migrated entry when a sign-in finally says who it is", () => {
    // The upgrade path EVERYBODY takes: migrated from the single-key store (no email), then re-signs in
    // on that same account. Matching on email alone never recognised it and appended a duplicate.
    installStorage({ "cc.deviceKey": "old-key", "cc.installId": "old-install" });
    expect(listAccounts()).toHaveLength(1);

    addAccount({ deviceKey: "fresh-key", installId: "fresh-install", email: "soren@example.com" });

    const accounts = listAccounts();
    expect(accounts).toHaveLength(1);
    expect(accounts[0].deviceKey).toBe("fresh-key");
    expect(accounts[0].email).toBe("soren@example.com");
  });

  it("matches an email whatever its casing", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "Soren@Example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "soren@example.com" });

    expect(listAccounts()).toHaveLength(1);
    expect(listAccounts()[0].deviceKey).toBe("k2");
  });

  it("NEVER collapses two accounts it cannot identify - that would destroy a device key", () => {
    // A self-host Gateway often cannot resolve an identity. Adopting an identity-less entry on an
    // identity-less enrollment would silently overwrite one account's key with another's.
    addAccount({ deviceKey: "k1", installId: "i1", email: null });
    addAccount({ deviceKey: "k2", installId: "i2", email: null });

    const accounts = listAccounts();
    expect(accounts).toHaveLength(2);
    expect(accounts.map((a) => a.deviceKey)).toEqual(["k1", "k2"]);
  });

  it("appends rather than guessing when TWO entries have no identity", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: null });
    addAccount({ deviceKey: "k2", installId: "i2", email: null });

    addAccount({ deviceKey: "k3", installId: "i3", email: "named@example.com" });

    expect(listAccounts()).toHaveLength(3);
  });
});

describe("a 401 removes the account that was REJECTED", () => {
  it("does not delete whichever account happens to be active when the answer lands", () => {
    // The scenario: a roster poll goes out as A, the person switches to B, then A's 401 arrives. The
    // rejected credential is A's - deleting the active account would remove B, a perfectly good login,
    // and leave the revoked A in place for the next poll to take out too.
    const a = addAccount({ deviceKey: "key-a", installId: "i1", email: "a@example.com" });
    const b = addAccount({ deviceKey: "key-b", installId: "i2", email: "b@example.com" });
    expect(activeAccount()?.id).toBe(b.id);

    // What onUnauthorized does with the credential the failed request carried.
    const rejected = listAccounts().find((x) => x.deviceKey === "key-a");
    expect(rejected?.id).toBe(a.id);
    removeAccount(rejected!.id);

    const left = listAccounts();
    expect(left).toHaveLength(1);
    expect(left[0].deviceKey).toBe("key-b");
    expect(activeAccount()?.id).toBe(b.id);
  });

  it("finds nothing to remove when the rejected credential is already gone", () => {
    addAccount({ deviceKey: "key-b", installId: "i2", email: "b@example.com" });

    // A stale answer about a credential no longer stored must remove NOTHING, or one revoked key walks
    // the browser through every account it holds.
    const rejected = listAccounts().find((x) => x.deviceKey === "key-a-long-since-removed");

    expect(rejected).toBeUndefined();
    expect(listAccounts()).toHaveLength(1);
  });
});
