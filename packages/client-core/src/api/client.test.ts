import { describe, it, expect, afterEach } from "vitest";
import {
  configureUnauthorizedRedirect,
  cockpitSignInRedirect,
  mobileSignInRedirect,
  resolveSignInTarget,
} from "./client";

// The shell-aware mid-session 401 redirect (issue #1024, retargeted by issue #1088). A 401 on the
// DESKTOP Cockpit must re-gate through the desktop's own /signin route - the shared client-core
// device-enrollment flow - NOT hard-navigate the whole app to the mobile PWA's /m/signin enrollment
// screen (which ejected the user from the desktop shell), and NOT to the retired /login token wall
// (issue #1088: after a website revoke the desktop returns to the shared sign-in flow, never to
// login.html). Each shell installs its own redirect at startup (configureUnauthorizedRedirect); these
// tests exercise the exact builder functions the shells install (cockpitSignInRedirect /
// mobileSignInRedirect) plus the resolver the 401 path calls (resolveSignInTarget), so the behavior is
// proven without a DOM.

describe("shell-aware unauthorized redirect", () => {
  // Restore the mobile default so one test's configuration never leaks into another.
  afterEach(() => configureUnauthorizedRedirect(mobileSignInRedirect));

  it("desktop 401 routes to the Cockpit /signin (with next=), never to /m/signin or /login", () => {
    // What apps/cockpit/src/main.tsx installs at startup.
    configureUnauthorizedRedirect(cockpitSignInRedirect);

    const target = resolveSignInTarget({ pathname: "/session/abc", search: "?tab=terminal" });

    expect(target).toBe(`/signin?next=${encodeURIComponent("/session/abc?tab=terminal")}`);
    // The bug #1024 fixed: the desktop path must NOT land on the mobile enrollment route.
    expect(target).not.toContain("/m/signin");
    expect(target.startsWith("/m/")).toBe(false);
    // The #1088 acceptance: a 401 returns to the shared sign-in flow, never to the token wall.
    expect(target.startsWith("/login")).toBe(false);
  });

  it("desktop redirect carries the current route forward so the user lands back after sign-in", () => {
    configureUnauthorizedRedirect(cockpitSignInRedirect);

    // A 401 while sitting on the roster root round-trips back to "/".
    expect(resolveSignInTarget({ pathname: "/", search: "" })).toBe(
      `/signin?next=${encodeURIComponent("/")}`,
    );
  });

  it("mobile 401 still routes to /m/signin", () => {
    // What apps/mobile/src/main.tsx installs at startup.
    configureUnauthorizedRedirect(mobileSignInRedirect);

    expect(resolveSignInTarget({ pathname: "/m/session/abc", search: "" })).toBe("/m/signin");
  });

  it("defaults to the mobile route when a shell installs nothing", () => {
    // The mobile shell historically owned client-core, so the unconfigured default stays /m/signin.
    expect(resolveSignInTarget({ pathname: "/m", search: "" })).toBe("/m/signin");
  });
});
