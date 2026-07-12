import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The poll-timeout hardening on the shared transport choke point (mobile-resilience mission, Phase 4):
// a repeating poll read passes a timeoutMs so a HUNG request (a half-open connection that never resolves
// or rejects) is aborted and reported as a bad connection, instead of sitting in flight forever and
// leaving the health signal stuck "good". A timeout is distinct from a caller-initiated abort (an
// unmount), which stays silent. The connection-health module is mocked so the reports are asserted
// directly; fake timers make the timeout deterministic.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { gatewayFetch } from "./client";

// A fetch that never settles on its own but rejects with an AbortError the moment its signal aborts -
// exactly how the browser fetch behaves for a hung request that is then aborted.
function hangingFetch(): typeof fetch {
  return ((_input: unknown, init?: RequestInit) =>
    new Promise((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => {
        const e = new Error("aborted");
        e.name = "AbortError";
        reject(e);
      });
    })) as unknown as typeof fetch;
}

describe("gatewayFetch poll timeout (Phase 4)", () => {
  const realFetch = globalThis.fetch;
  beforeEach(() => {
    vi.useFakeTimers();
    health.reachable.mockClear();
    health.unreachable.mockClear();
  });
  afterEach(() => {
    vi.useRealTimers();
    globalThis.fetch = realFetch;
  });

  it("aborts a hung request after the timeout and reports the Gateway unreachable", async () => {
    globalThis.fetch = hangingFetch();
    const call = gatewayFetch("/sessions", {}, { timeoutMs: 5000 });
    const assertion = expect(call).rejects.toMatchObject({ status: 504 });
    await vi.advanceTimersByTimeAsync(5000); // trip the timeout
    await assertion;
    expect(health.unreachable).toHaveBeenCalledTimes(1);
    expect(health.reachable).not.toHaveBeenCalled();
  });

  it("returns normally and reports reachable when the request beats the timeout", async () => {
    globalThis.fetch = (async () => ({ status: 200, ok: true }) as Response) as unknown as typeof fetch;
    const res = await gatewayFetch("/sessions", {}, { timeoutMs: 5000 });
    expect(res.status).toBe(200);
    expect(health.reachable).toHaveBeenCalledTimes(1);
    expect(health.unreachable).not.toHaveBeenCalled();
  });

  it("stays silent for a caller-initiated abort (an unmount), not a timeout", async () => {
    const controller = new AbortController();
    globalThis.fetch = hangingFetch();
    const call = gatewayFetch("/sessions", { signal: controller.signal }, { timeoutMs: 5000 });
    const assertion = expect(call).rejects.toMatchObject({ name: "AbortError" });
    controller.abort(); // the caller cancels before the timeout could fire
    await assertion;
    expect(health.unreachable).not.toHaveBeenCalled();
    expect(health.reachable).not.toHaveBeenCalled();
  });

  it("does not time out a request with no timeoutMs (one-shot writes are unchanged)", async () => {
    globalThis.fetch = (async () => ({ status: 200, ok: true }) as Response) as unknown as typeof fetch;
    const res = await gatewayFetch("/sessions/s/prompt", { method: "POST" });
    expect(res.status).toBe(200);
    expect(health.reachable).toHaveBeenCalledTimes(1);
  });
});
