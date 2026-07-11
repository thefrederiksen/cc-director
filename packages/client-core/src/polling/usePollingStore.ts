// The React binding for a shared polling store (issue #1239). useSyncExternalStore is exactly the right
// primitive: every component that calls this for the same store subscribes to the ONE loop and re-renders
// together when the snapshot changes, so all fleet pages show identical data at the same moment. The
// store handles the timer, the visibility gating, and keep-last-on-error; the component just reads state.
import { useSyncExternalStore } from "react";
import type { PollingStore, PollState } from "./pollingStore";

export function usePollingStore<T>(store: PollingStore<T>): PollState<T> {
  // No server snapshot variant: the Cockpit is a client-only single-page app, so the client snapshot
  // serves both. The store guarantees a stable reference between changes, which is what keeps this from
  // looping.
  return useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
}
