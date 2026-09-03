import { afterEach, describe, expect, it, vi } from "vitest";
import { getRuleFirings, getRules } from "./rulesClient";

// THE INSTRUMENT MUST NOT FAIL OPEN (fix round D, ruling D8). A Gateway answer that is missing the
// field the client asked for is a broken instrument, not an empty result. Read as an empty list it
// becomes "It has not fired yet" on the page - an absence-shaped check reporting a positive fact when
// the data never arrived. The rules list already refused a missing field; the firings read did not.
// Both are pinned here, against a fake fetch, so the two readers cannot drift apart again.

const realFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = realFetch;
});

function answering(body: unknown): void {
  globalThis.fetch = vi.fn(async () =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  ) as unknown as typeof fetch;
}

describe("the rule readers refuse an answer that is missing the field they asked for", () => {
  it("a firings answer with no firings field is an error, never an empty history", async () => {
    answering({});

    await expect(getRuleFirings("11111111-1111-1111-1111-111111111111")).rejects.toThrow(/firings/);
  });

  it("a rules answer with no rules field is an error, never an empty list", async () => {
    answering({});

    await expect(getRules()).rejects.toThrow(/rules/);
  });

  it("a firings answer with the field is read as the history it carries", async () => {
    answering({ firings: [] });

    expect(await getRuleFirings("11111111-1111-1111-1111-111111111111")).toEqual([]);
  });
});
