import { describe, expect, it } from "vitest";
import { createClock } from "./clock";
import type { PollTimers } from "./pollingStore";
import type { VisibilitySource } from "./visibility";

// The shared clock replaces the per-page 1-second re-render timers (issue #1239). Tested with the same
// injected seams as the polling store: a hand-driven interval, a controllable visibility source, and a
// fake "now" so the advancing value is deterministic.

function fakeTimers(): PollTimers & { tick(): void; armed(): number } {
  const handlers = new Map<number, () => void>();
  let nextId = 1;
  return {
    setInterval(handler) {
      const id = nextId++;
      handlers.set(id, handler);
      return id;
    },
    clearInterval(id) {
      handlers.delete(id);
    },
    tick() {
      for (const handler of [...handlers.values()]) handler();
    },
    armed() {
      return handlers.size;
    },
  };
}

function fakeVisibility(initial = true): VisibilitySource & { set(v: boolean): void } {
  let visible = initial;
  const listeners = new Set<() => void>();
  return {
    isVisible: () => visible,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    set(v) {
      visible = v;
      for (const listener of [...listeners]) listener();
    },
  };
}

// A hand-advanced clock so the value the ticker reports is deterministic.
function fakeNow() {
  let value = 1000;
  return { now: () => value, advance: (ms: number) => (value += ms) };
}

describe("createClock", () => {
  it("runs ONE timer shared across many subscribers and notifies them all on a tick", () => {
    const timers = fakeTimers();
    const clock = createClock({ intervalMs: 1000, now: fakeNow().now, visibility: fakeVisibility(), timers });

    let a = 0;
    let b = 0;
    clock.subscribe(() => a++);
    clock.subscribe(() => b++);

    expect(timers.armed()).toBe(1); // two subscribers, one timer
    expect(clock.isTicking()).toBe(true);

    timers.tick();
    expect(a).toBe(1);
    expect(b).toBe(1);
  });

  it("advances the reported time on each tick, and is stable between ticks", () => {
    const timers = fakeTimers();
    const nowSource = fakeNow();
    const clock = createClock({ intervalMs: 1000, now: nowSource.now, visibility: fakeVisibility(), timers });

    clock.subscribe(() => {});
    const before = clock.getSnapshot();
    expect(clock.getSnapshot()).toBe(before); // no tick: identical value

    nowSource.advance(1000);
    timers.tick();
    expect(clock.getSnapshot()).toBe(2000);
  });

  it("stops ticking when the last subscriber leaves", () => {
    const timers = fakeTimers();
    const clock = createClock({ intervalMs: 1000, now: fakeNow().now, visibility: fakeVisibility(), timers });

    const unsubA = clock.subscribe(() => {});
    const unsubB = clock.subscribe(() => {});
    expect(clock.isTicking()).toBe(true);

    unsubA();
    expect(clock.isTicking()).toBe(true); // B still watching
    unsubB();
    expect(clock.isTicking()).toBe(false);
    expect(timers.armed()).toBe(0);
  });

  it("pauses while the page is hidden and catches up on return to visible", () => {
    const timers = fakeTimers();
    const nowSource = fakeNow();
    const visibility = fakeVisibility(true);
    const clock = createClock({ intervalMs: 1000, now: nowSource.now, visibility, timers });

    let ticks = 0;
    clock.subscribe(() => ticks++);
    expect(clock.isTicking()).toBe(true);

    visibility.set(false);
    expect(clock.isTicking()).toBe(false);
    timers.tick();
    expect(ticks).toBe(0); // no re-render while hidden

    nowSource.advance(5000);
    visibility.set(true);
    expect(clock.isTicking()).toBe(true);
    expect(ticks).toBe(1); // caught the label up immediately on return
    expect(clock.getSnapshot()).toBe(6000);
  });
});
