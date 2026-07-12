import { useSyncExternalStore } from "react";

// The single connection-health signal for the whole app (mission: never clear good data just
// because the connection is bad). Every contact with the Gateway reports into this ONE store -
// success or failure - and the mobile shell reads the derived state to show (or hide) a single
// "bad connection" banner. It is fed ONLY at the transport choke points, never hand-wired per page:
// the fetch calls in api/client.ts all route through gatewayFetch, which calls reportGatewayReachable
// on any answered request and reportGatewayUnreachable when the request never reached a healthy
// backend. A later phase adds the terminal WebSocket's open/close events as a second feeder.
//
// The classification is the issue #1028 taxonomy already used for the friendly error copy: a fetch
// that throws (the browser could not reach the backend at all) or a front-proxy status that means the
// backend was unreachable (502/503/504, or a synthetic 0) is UNREACHABLE; every other answered status
// - including 4xx/500 application errors - proves the Gateway is reachable. A caller-initiated abort
// is not a connection signal and is not reported.
//
// The pattern mirrors the dictation status store (dictation/status.ts): a module-level store with a
// useSyncExternalStore subscriber hook, already proven in this codebase.

export type ConnectionState = "good" | "bad";

export interface ConnectionHealth {
  /** "good" while the last answered contact reached a healthy backend; "bad" once a contact could not
   *  reach one. Starts "good" so a freshly-loaded app shows no banner until something actually fails. */
  state: ConnectionState;
  /** Epoch milliseconds of the last time any Gateway contact reached a healthy backend, or 0 if this
   *  app has never had one. The banner reads this (only while "bad") to say how stale things are - so
   *  the value captured at the moment the connection went bad is the TRUE last-good time, moments ago,
   *  not the time the connection first became good. */
  lastGoodContactAt: number;
}

type Listener = () => void;

const _listeners = new Set<Listener>();

// The real last-good time, advanced on EVERY successful contact even while the state is already good.
// Kept separate from the published snapshot so a healthy run of polls does not churn React renders;
// it is folded into the snapshot only when the state actually flips.
let _lastGoodAt = 0;
let _state: ConnectionState = "good";

// A frozen snapshot recomputed only when the derived state flips, so useSyncExternalStore's getSnapshot
// returns a stable reference between renders (a fresh object every call would loop forever). The banner
// is hidden while "good", so not re-rendering on every successful poll is both cheaper and correct.
let _snapshot: ConnectionHealth = Object.freeze({ state: _state, lastGoodContactAt: _lastGoodAt });

function emit(): void {
  _snapshot = Object.freeze({ state: _state, lastGoodContactAt: _lastGoodAt });
  for (const l of _listeners) l();
}

function subscribe(listener: Listener): () => void {
  _listeners.add(listener);
  return () => {
    _listeners.delete(listener);
  };
}

/** Record that a Gateway contact reached a healthy backend. Advances the last-good time always, and
 *  flips the state back to "good" (notifying subscribers) if it was "bad". */
export function reportGatewayReachable(): void {
  _lastGoodAt = Date.now();
  if (_state !== "good") {
    _state = "good";
    emit();
  }
}

/** Record that a Gateway contact could not reach a healthy backend. Flips the state to "bad"
 *  (notifying subscribers) if it was "good"; the snapshot then carries the true last-good time. */
export function reportGatewayUnreachable(): void {
  if (_state !== "bad") {
    _state = "bad";
    emit();
  }
}

/** The current connection health (for non-React callers and tests). */
export function connectionHealth(): ConnectionHealth {
  return _snapshot;
}

/** React hook: the current connection health, re-rendering the caller whenever the derived state flips. */
export function useConnectionHealth(): ConnectionHealth {
  return useSyncExternalStore(subscribe, () => _snapshot, () => _snapshot);
}
