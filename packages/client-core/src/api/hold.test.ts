import { afterEach, describe, expect, it, vi } from "vitest";

// holdSession posts the desired { onHold } (plus an optional snoozeMinutes) and parses the Gateway's
// applied tri-state { onHold, pending }. The connection-health module is mocked so the shared transport
// does not touch a real signal, exactly as voiceModeAll.test.ts does.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { holdSession } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    json: async () => body,
  } as unknown as Response;
}

describe("holdSession returns the applied { onHold, pending } tri-state", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("carries pending=true for a DEFERRED snooze (working session) so the UI can show it", async () => {
    let seenUrl = "";
    let seenBody = "";
    globalThis.fetch = ((input: unknown, init?: RequestInit) => {
      seenUrl = String(input);
      seenBody = String(init?.body ?? "");
      // The Gateway defers a working session's snooze: accepted, not armed yet.
      return Promise.resolve(jsonResponse({ onHold: false, pending: true }));
    }) as unknown as typeof fetch;

    const result = await holdSession("sess-1", true, 60);

    expect(seenUrl).toContain("/sessions/sess-1/hold");
    expect(seenBody).toContain("\"onHold\":true");
    expect(seenBody).toContain("\"snoozeMinutes\":60");
    expect(result).toEqual({ onHold: false, pending: true });
  });

  it("reads onHold=true for an armed hold and defaults pending=false when the field is absent", async () => {
    globalThis.fetch = (() => Promise.resolve(jsonResponse({ onHold: true }))) as unknown as typeof fetch;
    expect(await holdSession("sess-1", true)).toEqual({ onHold: true, pending: false });
  });

  it("falls back to the requested onHold and pending=false on an empty body (old Gateway)", async () => {
    globalThis.fetch = (() => Promise.resolve(jsonResponse({}))) as unknown as typeof fetch;
    expect(await holdSession("sess-1", false)).toEqual({ onHold: false, pending: false });
  });
});
