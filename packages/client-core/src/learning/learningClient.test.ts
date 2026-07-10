import { describe, it, expect, afterEach, vi } from "vitest";
import { runWingmanAsk } from "./learningClient";
import { GATEWAY_UNREACHABLE_MESSAGE } from "../api/client";

// Regression tests for issue #1250: the Learning page's Ask-Wingman must never fail silently. The
// page ran the ask inside a try/finally with NO catch, so a thrown request (Gateway down, transport
// error) showed the user nothing at all. runWingmanAsk is the extracted, browser-free outcome mapper
// the page now uses; it must always resolve to exactly one of { answer, error } and must convert a
// thrown request into a visible error message rather than propagating it.
describe("runWingmanAsk", () => {
  afterEach(() => vi.unstubAllGlobals());

  // The core regression: a thrown request (the case the missing catch dropped on the floor) must come
  // back as an error message, never as a rejection and never as silence.
  it("surfaces a thrown request as an error message instead of failing silently", async () => {
    vi.stubGlobal("fetch", () => {
      throw new TypeError("Failed to fetch");
    });

    const outcome = await runWingmanAsk("What is DevThrottle?");

    expect(outcome.answer).toBeNull();
    expect(outcome.error).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    // The user-facing string must never carry the browser's raw transport wording.
    expect(outcome.error).not.toContain("Failed to fetch");
  });

  // A rejected fetch promise (asynchronous transport failure) is the same class of problem and must
  // resolve to an error, not reject out of runWingmanAsk.
  it("surfaces a rejected request as an error message", async () => {
    vi.stubGlobal("fetch", () => Promise.reject(new TypeError("Failed to fetch")));

    const outcome = await runWingmanAsk("What is DevThrottle?");

    expect(outcome.answer).toBeNull();
    expect(outcome.error).toBe(GATEWAY_UNREACHABLE_MESSAGE);
  });

  // A reachable Gateway that returns a non-2xx status must show the status-based error line.
  it("surfaces a non-2xx Gateway response as an error message", async () => {
    vi.stubGlobal("fetch", () =>
      Promise.resolve(
        new Response(JSON.stringify({}), { status: 503, headers: { "Content-Type": "application/json" } }),
      ),
    );

    const outcome = await runWingmanAsk("What is DevThrottle?");

    expect(outcome.answer).toBeNull();
    expect(outcome.error).not.toBeNull();
  });

  // An empty answer from an otherwise-successful call is a failure the page must name, not a blank box.
  it("names an empty answer instead of showing a blank box", async () => {
    vi.stubGlobal("fetch", () =>
      Promise.resolve(
        new Response(JSON.stringify({ spoken: "   ", error: null }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      ),
    );

    const outcome = await runWingmanAsk("What is DevThrottle?");

    expect(outcome.answer).toBeNull();
    expect(outcome.error).toBe("Wingman returned an empty answer.");
  });

  // The success path: the spoken answer comes through and no error is set.
  it("returns the spoken answer on success with no error", async () => {
    vi.stubGlobal("fetch", () =>
      Promise.resolve(
        new Response(JSON.stringify({ spoken: "DevThrottle is mission control.", error: null }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      ),
    );

    const outcome = await runWingmanAsk("What is DevThrottle?");

    expect(outcome.error).toBeNull();
    expect(outcome.answer).toBe("DevThrottle is mission control.");
  });
});
