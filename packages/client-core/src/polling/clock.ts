// One shared 1-second ticker for relative-time labels (issue #1239). Several Cockpit pages showed live
// "last seen 12s ago" text and each ran its own setInterval(..., 1000) purely to force a re-render as
// the clock advanced - two such timers on the Directors pages alone, and every one kept ticking on a
// hidden tab. This is the single ticker they share: it holds the current wall-clock time, advances it
// once a second while at least one page is watching AND the page is visible, and hands that value to
// useNow. One timer, no matter how many pages read it.
//
// Like the polling store it is framework-agnostic and every seam (the clock, the timer, the visibility
// source) is injectable, so the sharing and the visibility pause are unit-tested in Node.
import { documentVisibility, type VisibilitySource } from "./visibility";
import type { PollTimers } from "./pollingStore";

export interface Clock {
  subscribe(listener: () => void): () => void;
  /** The last tick's wall-clock time (milliseconds). Stable between ticks so useSyncExternalStore does
   *  not loop. */
  getSnapshot(): number;
  /** Whether the ticker is currently running (has subscribers AND the page is visible). */
  isTicking(): boolean;
}

export interface ClockOptions {
  intervalMs: number;
  now?: () => number;
  visibility?: VisibilitySource;
  timers?: PollTimers;
}

const defaultTimers: PollTimers = {
  setInterval: (handler, ms) => window.setInterval(handler, ms),
  clearInterval: (id) => window.clearInterval(id),
};

export function createClock(options: ClockOptions): Clock {
  const {
    intervalMs,
    now = () => Date.now(),
    visibility = documentVisibility(),
    timers = defaultTimers,
  } = options;

  const listeners = new Set<() => void>();
  let current = now();
  let timer: number | undefined;
  let unsubscribeVisibility: (() => void) | undefined;

  function tick(): void {
    current = now();
    for (const listener of listeners) listener();
  }

  function arm(): void {
    if (timer === undefined) timer = timers.setInterval(tick, intervalMs);
  }

  function disarm(): void {
    if (timer !== undefined) {
      timers.clearInterval(timer);
      timer = undefined;
    }
  }

  function onVisibilityChange(): void {
    if (listeners.size === 0) return;
    if (visibility.isVisible()) {
      // Returned to visible: catch the label up immediately, then resume ticking.
      tick();
      arm();
    } else {
      disarm();
    }
  }

  function start(): void {
    unsubscribeVisibility = visibility.subscribe(onVisibilityChange);
    if (visibility.isVisible()) {
      current = now();
      arm();
    }
  }

  function stop(): void {
    disarm();
    unsubscribeVisibility?.();
    unsubscribeVisibility = undefined;
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      if (listeners.size === 1) start();
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) stop();
      };
    },
    getSnapshot() {
      return current;
    },
    isTicking() {
      return timer !== undefined;
    },
  };
}

// The process-wide 1-second ticker every relative-time label shares (see useNow).
export const sharedClock: Clock = createClock({ intervalMs: 1000 });
