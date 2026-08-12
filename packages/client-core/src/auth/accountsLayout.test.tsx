// The layout of the Accounts panel, checked against the REAL stylesheet (devthrottle_internal #1507,
// #1509).
//
// This file exists because of a defect that shipped to a real phone on 2026-08-11. The account row ran
// off the right edge of the screen: .accts-label and .accts-sub are <span> elements, accounts.css never
// declared display:block on them, and an inline box ignores overflow / text-overflow / width - so the
// email and the status line rendered on ONE line and the ellipsis that was supposed to contain them did
// nothing at all.
//
// Every existing test passed through it. They assert what the panel SAYS - the labels, the confirmation
// wording, which handler fires - and text content is exactly what stays correct while a layout breaks.
// So the gap was not "a missing assertion", it was a whole class of claim nobody was making.
//
// What this does about it, and what it honestly cannot: vitest stubs CSS imports, so the component's own
// `import "./accounts.css"` contributes nothing to the document. Here the stylesheet is read off disk and
// injected, so getComputedStyle resolves the SHIPPED rules against the SHIPPED markup - which is what
// catches a selector that does not match, or a declaration that was never written.
//
// jsdom does not lay out or paint. It cannot tell you a row is 40 pixels too wide for a 412-pixel phone.
// So these pin the DECLARATIONS that make truncation possible, not the pixels - a real device or a
// headless browser is still the only thing that proves it fits. Said plainly so a green run here is not
// mistaken for that.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { AccountsPanel } from "./AccountsPanel";
import { addAccount, removeAllAccounts } from "./accountStore";

vi.mock("./accountActions", () => ({
  switchAccount: () => {},
  signOutAccount: () => {},
  signOutAllAccounts: () => {},
}));

const HERE = dirname(fileURLToPath(import.meta.url));

// The stylesheet the app actually ships, put into the document so computed style means something. If the
// file is ever renamed or split this read throws, which is the correct outcome - a silently absent
// stylesheet would make every assertion below pass against unstyled markup.
function installRealStylesheet(): void {
  const css = readFileSync(join(HERE, "accounts.css"), "utf8");
  expect(css.length).toBeGreaterThan(0);
  const style = document.createElement("style");
  style.textContent = css;
  document.head.appendChild(style);
}

// A long address, because the defect only showed with one. A short label fits either way, which is how
// this survived every test and the author's own reading of the screen.
const LONG_EMAIL = "soren@centerconsulting.com";

let uuidCounter = 0;
beforeEach(() => {
  uuidCounter = 0;
  vi.stubGlobal("crypto", { randomUUID: () => `id-${++uuidCounter}` });
  localStorage.clear();
  removeAllAccounts();
  document.head.querySelectorAll("style").forEach((s) => s.remove());
  installRealStylesheet();
});

afterEach(cleanup);

describe("the account row, against the real stylesheet", () => {
  beforeEach(() => {
    addAccount({ deviceKey: "k1", installId: "i1", email: LONG_EMAIL });
  });

  it("stacks the label and the status line instead of running them together on one line", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    const label = document.querySelector(".accts-label") as HTMLElement;
    const sub = document.querySelector(".accts-sub") as HTMLElement;

    // THE REGRESSION. Both were inline, so they sat side by side and overflowed the card.
    expect(getComputedStyle(label).display).toBe("block");
    expect(getComputedStyle(sub).display).toBe("block");
  });

  it("arms the ellipsis on both lines, which an inline box would have ignored", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    for (const selector of [".accts-label", ".accts-sub"]) {
      const el = document.querySelector(selector) as HTMLElement;
      const style = getComputedStyle(el);
      expect(style.overflow).toBe("hidden");
      expect(style.textOverflow).toBe("ellipsis");
      expect(style.whiteSpace).toBe("nowrap");
    }
  });

  it("lets the row shrink below the width of a long address", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    // Without min-width:0 a flex item refuses to shrink past its content, so the ellipsis above never
    // engages however correct the rest of the declarations are.
    const body = document.querySelector(".accts-body") as HTMLElement;
    expect(getComputedStyle(body).minWidth).toBe("0");
  });

  it("marks the active account with a drawn dot carrying no text of its own", () => {
    render(<AccountsPanel onAddAccount={() => {}} />);

    const mark = document.querySelector(".accts-mark") as HTMLElement;
    // No character: nothing for a screen reader to read out beside the name, and nothing competing with
    // the label for width. Which account is active is stated in words on the line below.
    expect(mark.textContent).toBe("");
    expect(getComputedStyle(mark).borderRadius).toBe("50%");
  });
});

describe("buttons", () => {
  it("carries no account address in a button label, on either the one or the two account screen", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: LONG_EMAIL });
    render(<AccountsPanel onAddAccount={() => {}} />);

    // An email is unbounded, and in a button it wrapped to two lines on a phone. So no ACTION button may
    // contain one - the account is named in the confirmation instead, where prose can wrap safely.
    //
    // Scoped to .accts-btn deliberately. The account row is a <button> too and SHOWS the address on
    // purpose: that is the thing you are choosing between, and it is a truncating block rather than a
    // label. A blanket "no button contains @" fails on it, which is how this assertion was first written.
    const actionButtons = () => Array.from(document.querySelectorAll(".accts-btn"));

    expect(actionButtons().length).toBeGreaterThan(0);
    for (const button of actionButtons()) {
      expect(button.textContent ?? "").not.toContain("@");
    }

    cleanup();
    addAccount({ deviceKey: "k2", installId: "i2", email: "second@example.com" });
    render(<AccountsPanel onAddAccount={() => {}} />);

    expect(actionButtons().length).toBeGreaterThan(0);
    for (const button of actionButtons()) {
      expect(button.textContent ?? "").not.toContain("@");
    }
  });

  it("holds every button label to one line", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: LONG_EMAIL });
    render(<AccountsPanel onAddAccount={() => {}} />);

    for (const button of screen.getAllByRole("button", { name: /add account|sign out/i })) {
      expect(getComputedStyle(button).whiteSpace).toBe("nowrap");
    }
  });

  it("lets the two confirmation buttons share one row without summing past it", () => {
    addAccount({ deviceKey: "k1", installId: "i1", email: LONG_EMAIL });
    render(<AccountsPanel onAddAccount={() => {}} />);
    screen.getByRole("button", { name: "Sign out" }).click();

    // Each is a 100%-width block in its own right, so side by side they need an explicit zero basis.
    for (const button of document.querySelectorAll(".accts-confirm-row .accts-btn")) {
      const style = getComputedStyle(button as HTMLElement);
      expect(style.flexBasis).toBe("0px");
      expect(style.minWidth).toBe("0px");
    }
  });
});
