import { describe, expect, it, vi } from "vitest";
import { SIGN_IN_START_PATH, beginSignIn } from "./accountClient";

// Signing the Gateway in from the Cockpit has two load-bearing properties, and both are easy to regress
// into a version that hangs forever with no visible error:
//
//  1. It must target /account/sign-in-start, NOT the old POST /account/sign-in. The old endpoint always
//     runs the Gateway's browser LOOPBACK sign-in - a browser on the GATEWAY HOST's desktop waiting on
//     127.0.0.1 - which a Cockpit on any other machine can never reach. That was the live bug.
//  2. It must NAVIGATE via a form submission, not fetch. The endpoint answers a remote caller with a 302
//     to devthrottle.com; only a navigation lets the browser follow it and show the person the sign-in
//     page. A fetch would follow the redirect invisibly in the background.
//
// Both failures look identical to a user - the button never completes - so they are pinned here. The
// fake document keeps this a pure unit test: client-core has no jsdom, and this needs no real DOM.

function fakeDom() {
  const form = { method: "", action: "", submit: vi.fn() };
  const appended: unknown[] = [];
  const doc = {
    createElement: vi.fn(() => form),
    body: { appendChild: (n: unknown) => appended.push(n) },
  } as unknown as Document;
  return { doc, form, appended };
}

describe("beginSignIn", () => {
  it("submits a form POST to the public sign-in start front door", () => {
    const { doc, form, appended } = fakeDom();

    beginSignIn(doc);

    expect(form.method).toBe("POST");
    expect(form.action).toBe(SIGN_IN_START_PATH);
    expect(form.submit).toHaveBeenCalledOnce();
    // The form must be in the document before submit(); a detached form does not navigate.
    expect(appended).toEqual([form]);
  });

  it("does not target the loopback sign-in endpoint", () => {
    const { doc, form } = fakeDom();

    beginSignIn(doc);

    expect(form.action).not.toBe("/account/sign-in");
  });

  it("navigates rather than fetching, so the browser can follow the 302 to the cloud", () => {
    const { doc } = fakeDom();
    const fetchSpy = vi.spyOn(globalThis, "fetch");

    beginSignIn(doc);

    expect(fetchSpy).not.toHaveBeenCalled();
    fetchSpy.mockRestore();
  });
});
