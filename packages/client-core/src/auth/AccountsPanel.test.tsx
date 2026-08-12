// The Accounts panel (devthrottle_internal #1507, #1509) - the screen that finally gives the mobile app
// a way to sign out, and both surfaces a way to switch between two logins.
//
// What these actually guard: that a sign-out is never one tap (the whole reason the menu row links here
// instead of acting), that the confirmation SAYS which of the two outcomes will happen, and that the
// panel is honest about a browser holding no account at all.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { AccountsPanel } from "./AccountsPanel";
import { addAccount, removeAllAccounts } from "./accountStore";

// accountActions ends every path in a hard navigation, which jsdom cannot perform. Stubbing the module
// keeps these tests about the PANEL - what it offers and what it says - and leaves where it navigates to
// accountActions itself.
//
// THE STUBS MUST RESOLVE TO A RESULT, exactly like the real actions do. A bare vi.fn() resolves to
// undefined, and the panel then reads .ok off it - which throws ASYNCHRONOUSLY, after the assertion has
// already passed. Vitest reports that as an unhandled error rather than a failing test, so the suite
// prints "961 passed" beside "Errors 2" and a summary skimmed for failures alone reads as green. It
// reached main that way, and the web job on main is what caught it.
// The implementations take the SAME arguments the call sites pass. vi.fn(async () => ...) infers a
// ZERO-argument mock, so calling it with an id is a type error (TS2554) - which vitest never sees,
// because it does not typecheck. It broke the .NET build too: the Gateway project runs the workspace
// typecheck as part of its own build, so one TypeScript error reddens both jobs.
const switchAccount = vi.fn((_id: string) => {});
const signOutAccount = vi.fn(async (_id: string) => ({ ok: true as const }));
const signOutAllAccounts = vi.fn(async () => ({ ok: true as const }));
vi.mock("./accountActions", () => ({
  switchAccount: (id: string) => switchAccount(id),
  signOutAccount: (id: string) => signOutAccount(id),
  signOutAllAccounts: () => signOutAllAccounts(),
}));

let uuidCounter = 0;
beforeEach(() => {
  vi.clearAllMocks();
  uuidCounter = 0;
  vi.stubGlobal("crypto", { randomUUID: () => `id-${++uuidCounter}` });
  localStorage.clear();
  removeAllAccounts();
});

// This library installs no global testing-library setup, so each rendering test unmounts its own tree -
// without it the previous test's panel is still in the document and every query matches twice.
afterEach(cleanup);

describe("a browser with no account", () => {
  it("says so, and offers to sign in rather than showing an empty list", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.getByText(/not signed in to any account/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign in" })).toBeTruthy();
  });
});

describe("a browser with one account", () => {
  beforeEach(() => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "personal@example.com" });
  });

  it("names the account and marks it as the one in use", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.getByText("personal@example.com")).toBeTruthy();
    expect(screen.getByText("Signed in now")).toBeTruthy();
  });

  it("offers Add account and a sign-out that names the account", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.getByRole("button", { name: "Add account" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
  });

  it("does NOT offer sign out of all - there is only one", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.queryByRole("button", { name: /all accounts/i })).toBeNull();
  });

  it("NEVER signs out on the first tap - it asks first", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(signOutAccount).not.toHaveBeenCalled();
    expect(screen.getByText(/You will need to sign in through devthrottle.com/i)).toBeTruthy();
  });

  it("signs out on the confirmation, and can be backed out of", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(signOutAccount).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, sign out" }));
    expect(signOutAccount).toHaveBeenCalledTimes(1);
  });

  it("replaces the actions with the question, and the committing button answers it", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));

    // The trigger is GONE while the question is up - the actions are replaced, not stacked - so exactly
    // one sign-out control is on screen and it is the one that commits. It reads as an answer rather
    // than as the same button appearing twice.
    expect(screen.getAllByRole("button", { name: /sign out/i })).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Yes, sign out" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add account" })).toBeNull();
  });

  it("hands Add account back to the shell, which owns the sign-in route", () => {
    const onAddAccount = vi.fn();
    render(<AccountsPanel onAddAccount={onAddAccount} />);

    fireEvent.click(screen.getByRole("button", { name: "Add account" }));

    expect(onAddAccount).toHaveBeenCalledTimes(1);
  });
});

describe("a browser with two accounts", () => {
  beforeEach(() => {
    addAccount({ deviceKey: "k1", installId: "i1", email: "personal@example.com" });
    addAccount({ deviceKey: "k2", installId: "i2", email: "work@example.com" });
  });

  it("lists both, with the newest one active", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.getByText("personal@example.com")).toBeTruthy();
    expect(screen.getByText("work@example.com")).toBeTruthy();
    expect(screen.getByText("Signed in now")).toBeTruthy();
  });

  it("switches to the other account on a tap", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByText("personal@example.com"));

    expect(switchAccount).toHaveBeenCalledTimes(1);
  });

  it("cannot switch to the account already in use", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByText("work@example.com"));

    expect(switchAccount).not.toHaveBeenCalled();
  });

  it("says switching needs no password and no connection, because that is not obvious", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(screen.getByText(/works with no connection/i)).toBeTruthy();
  });

  it("promises the OTHER account survives - the outcome that differs from a single-login sign-out", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Sign out of this account" }));

    expect(screen.getByText(/You stay signed in to your other account/i)).toBeTruthy();
  });

  it("offers signing out of all of them, and warns that each needs signing in again", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Sign out of all" }));
    expect(signOutAllAccounts).not.toHaveBeenCalled();
    expect(screen.getByText(/again for each one/i)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Yes, sign out" }));
    expect(signOutAllAccounts).toHaveBeenCalledTimes(1);
  });

  it("says the device stays on the account at devthrottle.com, so a sign-out is not read as a revoke", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Sign out of this account" }));

    expect(screen.getByText(/stays on your account at devthrottle.com/i)).toBeTruthy();
  });
});
