import { describe, it, expect, vi } from "vitest";
import { confirmThenApply } from "./queueActions";

// Regression tests for issue #1252: the prompt queue lost user text when the server call failed because
// the local side effect (Pop pasting into the composer, Save tearing down the edit box) ran BEFORE the
// server verb confirmed. confirmThenApply pins the fixed order: the side effect fires only after, and
// only if, the server verb succeeds.
describe("confirmThenApply", () => {
  it("applies the side effect when the verb succeeds", async () => {
    const applyOnSuccess = vi.fn();

    const result = await confirmThenApply(() => Promise.resolve(true), applyOnSuccess);

    expect(result).toBe(true);
    expect(applyOnSuccess).toHaveBeenCalledTimes(1);
  });

  it("does NOT apply the side effect when the verb fails - text is never lost", async () => {
    const applyOnSuccess = vi.fn();

    const result = await confirmThenApply(() => Promise.resolve(false), applyOnSuccess);

    expect(result).toBe(false);
    expect(applyOnSuccess).not.toHaveBeenCalled();
  });

  it("waits for the verb to resolve before applying the side effect (never optimistic)", async () => {
    const order: string[] = [];
    const runVerb = () =>
      new Promise<boolean>((resolve) => {
        order.push("verb-start");
        // Resolve on a later microtask so an optimistic (pre-await) side effect would be observable.
        Promise.resolve().then(() => {
          order.push("verb-resolved");
          resolve(true);
        });
      });
    const applyOnSuccess = () => order.push("apply");

    await confirmThenApply(runVerb, applyOnSuccess);

    expect(order).toEqual(["verb-start", "verb-resolved", "apply"]);
  });
});
