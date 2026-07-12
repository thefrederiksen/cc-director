import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connectionHealth, reportGatewayReachable, reportGatewayUnreachable } from "./health";

// The store is a module-level singleton, so each test first drives it to a known-good baseline via the
// public report functions (there is no test-only reset backdoor). Fake timers make Date.now
// deterministic so the "last good time captured at the moment it goes bad" behaviour is testable.
describe("connection health store", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000_000);
    // Force a known baseline via a real flip (bad -> good) so the published snapshot folds in the
    // known last-good time. A plain reachable call from an already-good state would not re-publish
    // (that is the store's contract: it only emits a new snapshot when the state actually flips).
    reportGatewayUnreachable();
    reportGatewayReachable();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("reads good with a last-good time after a reachable contact", () => {
    const h = connectionHealth();
    expect(h.state).toBe("good");
    expect(h.lastGoodContactAt).toBe(1_000_000);
  });

  it("flips to bad on an unreachable contact", () => {
    reportGatewayUnreachable();
    expect(connectionHealth().state).toBe("bad");
  });

  it("keeps a stable snapshot reference while the state does not flip (useSyncExternalStore contract)", () => {
    const first = connectionHealth();
    reportGatewayReachable(); // good -> good: no flip, so no new snapshot
    expect(connectionHealth()).toBe(first);
    reportGatewayUnreachable(); // good -> bad: a flip, so a new snapshot
    expect(connectionHealth()).not.toBe(first);
  });

  it("captures the TRUE last-good time (moments before the drop), not the flip-to-good time", () => {
    // Go bad, then good at t=2_000_000 (this is the flip-to-good time)...
    reportGatewayUnreachable();
    vi.setSystemTime(2_000_000);
    reportGatewayReachable();
    // ...keep succeeding up to t=2_050_000 without any flip (state stays good, no new snapshot)...
    vi.setSystemTime(2_050_000);
    reportGatewayReachable();
    // ...then the connection drops. The banner must show the LAST success (2_050_000), not 2_000_000.
    reportGatewayUnreachable();
    expect(connectionHealth().lastGoodContactAt).toBe(2_050_000);
  });

  it("recovers to good and advances the last-good time on the next reachable contact", () => {
    reportGatewayUnreachable();
    vi.setSystemTime(3_000_000);
    reportGatewayReachable();
    const h = connectionHealth();
    expect(h.state).toBe("good");
    expect(h.lastGoodContactAt).toBe(3_000_000);
  });

  it("does not emit a new snapshot on repeated unreachable contacts", () => {
    reportGatewayUnreachable();
    const bad = connectionHealth();
    reportGatewayUnreachable();
    expect(connectionHealth()).toBe(bad);
  });
});
