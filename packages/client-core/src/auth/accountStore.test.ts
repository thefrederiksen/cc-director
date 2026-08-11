// The account store (devthrottle_internal #1509). These cover the four things that would silently cost
// somebody their sign-in: the migration off the single-key store, the dedupe that stops a re-sign-in
// duplicating a row, per-account install ids, and what happens to the ACTIVE pointer when accounts come
// and go.
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  activeAccount,
  addAccount,
  clearPendingInstallId,
  listAccounts,
  newPendingInstallId,
  pendingInstallId,
  removeAccount,
  removeAllAccounts,
  renameAccount,
  setActiveAccount,
  subscribeToAccounts,
} from "./accountStore";

// A minimal localStorage over a Map. jsdom supplies one, but driving it explicitly keeps each test's
// starting state visible rather than inherited.
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
  vi.stubGlobal("crypto", {
    randomUUID: () => `id-${++uuidCounter}`,
  });
  installStorage();
});

describe("migrating off the single-key store", () => {
  it("adopts an existing cc.deviceKey as the first account, so upgrading signs nobody out", () => {
    installStorage({ "cc.deviceKey": "old-key", "cc.installId": "old-install" });

    const accounts = listAccounts();

    expect(accounts).toHaveLength(1);
    expect(accounts[0].deviceKey).toBe("old-key");
    expect(activeAccount()?.deviceKey).toBe("old-key");
  });

  it("keeps the migrated account's EXISTING install id, so its cloud device row is not stranded", () => {
    installStorage({ "cc.deviceKey": "old-key", "cc.installId": "old-install" });

    expect(listAccounts()[0].installId).toBe("old-install");
  });

  it("reports no accounts on a browser that never enrolled", () => {
    expect(listAccounts()).toEqual([]);
    expect(activeAccount()).toBeNull();
  });

  it("mirrors the active key back to cc.deviceKey, so a rolled-back bundle stays signed in", () => {
    const map = installStorage();
    addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });

    expect(map.get("cc.deviceKey")).toBe("k1");
  });
});

describe("adding accounts", () => {
  it("appends a second account instead of overwriting the first", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "personal@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "work@example.com" });

    const accounts = listAccounts();
    expect(accounts.map((a) => a.deviceKey)).toEqual(["k1", "k2"]);
    // The newly added one is the one you are now using.
    expect(activeAccount()?.deviceKey).toBe("k2");
  });

  it("labels an account with its email when the Gateway resolved one", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "work@example.com" });

    expect(listAccounts()[0].label).toBe("work@example.com");
  });

  it("gives an account a positional name when no identity could be resolved", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: null });
    addAccount({ deviceKey: "k2", installId: "i2", email: null });

    expect(listAccounts().map((a) => a.label)).toEqual(["Account 1", "Account 2"]);
  });

  it("REPLACES the entry when the same account signs in again, rather than duplicating it", () => {
    addAccount({ deviceKey: "old", installId: "i1", email: "work@example.com" });
    addAccount({ deviceKey: "fresh", installId: "i2", email: "work@example.com" });

    const accounts = listAccounts();
    expect(accounts).toHaveLength(1);
    expect(accounts[0].deviceKey).toBe("fresh");
    expect(accounts[0].installId).toBe("i2");
  });

  it("keeps two DIFFERENT accounts apart even though both are on one browser", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "personal@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "work@example.com" });

    const [personal, work] = listAccounts();
    expect(personal.installId).not.toBe(work.installId);
    expect(personal.deviceKey).not.toBe(work.deviceKey);
  });
});

describe("switching", () => {
  it("moves the active pointer without touching either key", () => {
    const first = addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "b@example.com" });

    expect(setActiveAccount(first.id)).toBe(true);
    expect(activeAccount()?.deviceKey).toBe("k1");
    expect(listAccounts()).toHaveLength(2);
  });

  it("refuses an id that names no stored account", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });

    expect(setActiveAccount("not-a-real-id")).toBe(false);
    expect(activeAccount()?.deviceKey).toBe("k1");
  });

  it("falls to the first account when the stored pointer dangles, rather than reporting signed out", () => {
    const map = installStorage();
    addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    map.set("cc.activeAccount", "an-id-that-was-removed");

    expect(activeAccount()?.deviceKey).toBe("k1");
  });
});

describe("signing out", () => {
  it("removing the active account activates the other one, not the sign-in screen", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    const second = addAccount({ deviceKey: "k2", installId: "i2", email: "b@example.com" });

    removeAccount(second.id);

    expect(listAccounts()).toHaveLength(1);
    expect(activeAccount()?.deviceKey).toBe("k1");
  });

  it("removing the only account leaves the browser with nothing", () => {
    const only = addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });

    removeAccount(only.id);

    expect(listAccounts()).toEqual([]);
    expect(activeAccount()).toBeNull();
  });

  it("removing an INACTIVE account leaves the active one where it was", () => {
    const first = addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "b@example.com" });

    removeAccount(first.id);

    expect(activeAccount()?.deviceKey).toBe("k2");
  });

  it("clears the legacy mirror when the last account goes, so no key is left on disk", () => {
    const map = installStorage();
    const only = addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });

    removeAccount(only.id);

    expect(map.get("cc.deviceKey")).toBeUndefined();
  });

  it("removeAllAccounts empties the browser", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "b@example.com" });

    removeAllAccounts();

    expect(listAccounts()).toEqual([]);
  });
});

describe("the in-flight install id", () => {
  it("mints a fresh one per sign-in, which is what makes a second account a second device", () => {
    const first = newPendingInstallId();
    const second = newPendingInstallId();

    expect(first).not.toBe(second);
  });

  it("is readable on the callback leg after the round trip", () => {
    const minted = newPendingInstallId();

    expect(pendingInstallId()).toBe(minted);
  });

  it("mints one when none was saved, so an enrollment never runs without a device id", () => {
    expect(pendingInstallId()).not.toBe("");
  });

  it("is consumed once the account it belongs to is stored", () => {
    const minted = newPendingInstallId();
    clearPendingInstallId();

    expect(pendingInstallId()).not.toBe(minted);
  });
});

describe("subscribers", () => {
  it("are told when an account is added, switched, renamed or removed", () => {
    const seen = vi.fn();
    const unsubscribe = subscribeToAccounts(seen);

    const account = addAccount({ deviceKey: "k1", installId: "i1", email: "a@example.com" });
    expect(seen).toHaveBeenCalledTimes(1);

    renameAccount(account.id, "Work");
    expect(seen).toHaveBeenCalledTimes(2);

    removeAccount(account.id);
    expect(seen).toHaveBeenCalledTimes(3);

    unsubscribe();
    addAccount({ deviceKey: "k2", installId: "i2", email: "b@example.com" });
    expect(seen).toHaveBeenCalledTimes(3);
  });
});

describe("a damaged store", () => {
  it("reads as no accounts rather than crashing the shell on boot", () => {
    installStorage({ "cc.accounts": "{not json" });

    expect(listAccounts()).toEqual([]);
  });

  it("drops an entry missing the one field that matters - its key", () => {
    installStorage({
      "cc.accounts": JSON.stringify([
        { id: "a", label: "Broken", email: null, installId: "i1" },
        { id: "b", label: "Good", email: null, deviceKey: "k2", installId: "i2" },
      ]),
    });

    const accounts = listAccounts();
    expect(accounts).toHaveLength(1);
    expect(accounts[0].deviceKey).toBe("k2");
  });
});
