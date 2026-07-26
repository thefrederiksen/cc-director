// The browser side of the client error channel (client error logging build): every error a shell shows
// the user - and every uncaught browser error - is ALSO reported to the Gateway (POST /client-errors),
// where it lands in the server log and the tenant's queryable recent ring. Before this, a browser error
// existed only on the user's screen and in devtools, and an agent had to ask the user to read it back.
//
// Design constraints, all deliberate:
// - FIRE-AND-FORGET and swallow-everything: reporting an error must never throw, never block the UI,
//   and never itself become an error the user sees. A raw fetch with keepalive (NOT gatewayFetch: a
//   report during a bad connection must not feed the connection-health store or retry machinery).
// - CLIENT-SIDE rate cap in addition to the server's: an error thrown in a render loop would otherwise
//   fire hundreds of identical reports a second before the server ever saw the first.
// - The report carries what a debugging agent needs (surface, page, message, detail, stack) and nothing
//   personal beyond it.

import { authHeaders, gatewayErrorMessage } from "../api/client";

/** Client-side cap: at most this many reports per minute; the rest are counted and dropped. The server
 *  enforces its own cap too - this one exists so a render-loop error does not even leave the browser. */
const MAX_REPORTS_PER_MINUTE = 20;

let windowStartMs = 0;
let reportsInWindow = 0;
let droppedInWindow = 0;

/** Decide whether one more report may leave the browser this minute. Exported for unit tests. */
export function admitReport(nowMs: number): boolean {
  if (nowMs - windowStartMs >= 60_000) {
    windowStartMs = nowMs;
    reportsInWindow = 0;
    droppedInWindow = 0;
  }
  if (reportsInWindow < MAX_REPORTS_PER_MINUTE) {
    reportsInWindow++;
    return true;
  }
  droppedInWindow++;
  if (droppedInWindow === 1) {
    console.warn("[client-errors] rate cap reached; further reports this minute stay local");
  }
  return false;
}

/** Reset the rate window (unit tests only). */
export function resetReportWindow(): void {
  windowStartMs = 0;
  reportsInWindow = 0;
  droppedInWindow = 0;
}

/**
 * Report one error the user was shown (or an uncaught one) to the Gateway. Never throws; never blocks.
 * `surface` is the app plus area ("cockpit-assistant", "mobile-global"); `page` is the current route.
 */
export function reportClientError(surface: string, page: string, message: string, err?: unknown): void {
  try {
    if (!admitReport(Date.now())) return;
    const detail = err instanceof Error ? `${err.name}: ${err.message}` : err !== undefined ? String(err) : "";
    const stack = err instanceof Error && typeof err.stack === "string" ? err.stack : "";
    void fetch("/client-errors", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify({ surface, page, message, detail, stack }),
      keepalive: true,
    }).catch(() => {
      // Unreachable Gateway: nothing to do - the error is already on screen, and reporting must not
      // create follow-on noise. The server-side record simply does not exist for this one.
    });
  } catch {
    // Reporting is best-effort by definition; swallowing here is the contract, not a shortcut.
  }
}

let globalInstalled = false;

/**
 * Install the app-wide catchers for errors NOBODY handled: window "error" (uncaught throw) and
 * "unhandledrejection" (un-awaited promise failure). Call once from the shell's entry point. Errors a
 * page handles and renders keep their own explicit reportClientError call sites - the global hook is
 * the net under everything else, not a replacement for them.
 */
export function installGlobalErrorReporting(surface: string): void {
  if (globalInstalled || typeof window === "undefined") return;
  globalInstalled = true;
  window.addEventListener("error", (event) => {
    reportClientError(
      `${surface}-global`,
      window.location.pathname,
      event.message || "Uncaught error",
      event.error ?? undefined,
    );
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason: unknown = event.reason;
    const message = reason instanceof Error ? reason.message : String(reason ?? "Unhandled promise rejection");
    reportClientError(`${surface}-global`, window.location.pathname, message, reason);
  });
}

/**
 * Render AND report, in one act (issue #2189).
 *
 * This is the ONE way a shell turns a caught error into on-screen text. It returns the sentence to
 * display and reports the same failure to the Gateway, so an error the user is looking at can no
 * longer exist only on the user's screen.
 *
 * The reason this exists as a function rather than as a rule: the global catcher in
 * installGlobalErrorReporting only sees errors NOBODY handled. An error a page catches and renders -
 * which is nearly all of them - never reaches it. Before this, the whole product had exactly one
 * explicit report call site, so a failed image attach was invisible to the server and finding it
 * meant grepping a fifty-four megabyte Gateway log by hand. A call site that formats a message
 * without reporting it is the bug this function exists to prevent, so the two cannot be separated.
 *
 * `action` names what the user was trying to do, in their terms: "attach the image", "send that to
 * the session". It shapes the sentence AND labels the server-side record.
 */
export function describeAndReport(surface: string, action: string, err: unknown): string {
  const message = gatewayErrorMessage(err, action);
  const page = typeof window === "undefined" ? "" : window.location.pathname;
  reportClientError(surface, page, `could not ${action}: ${message}`, err);
  return message;
}
