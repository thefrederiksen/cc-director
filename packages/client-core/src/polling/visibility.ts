// Page visibility, abstracted so the polling primitives can gate on it and the tests can drive it
// without a DOM (issue #1239). Every Cockpit poll loop used to run whether or not the browser tab was
// visible, so a backgrounded tab kept hammering the Gateway forever. The polling store and the shared
// clock read visibility through this seam: while the document is hidden they go quiet, and when it
// returns to visible they refetch immediately and resume.

// A source of "is the page visible right now" plus a change notification. The default reads the real
// document; tests pass a controllable fake so the visibility transitions are deterministic.
export interface VisibilitySource {
  /** True while the page is visible to the user (foreground tab, not minimized). */
  isVisible(): boolean;
  /** Register for visibility transitions; returns an unsubscribe. */
  subscribe(listener: () => void): () => void;
}

// The real page-visibility source, backed by document.visibilityState and the "visibilitychange"
// event. Used by the shared roster store and the shared clock in the browser. When the document is
// unavailable (a non-DOM host such as a unit test that forgot to inject a source), it reports visible
// and never notifies - the caller then polls exactly as it did before this change, so nothing regresses.
export function documentVisibility(): VisibilitySource {
  const hasDocument = typeof document !== "undefined";
  return {
    isVisible() {
      // Treat an unknown state as visible: only an explicit "hidden" pauses polling.
      return !hasDocument || document.visibilityState !== "hidden";
    },
    subscribe(listener) {
      if (!hasDocument) return () => {};
      document.addEventListener("visibilitychange", listener);
      return () => document.removeEventListener("visibilitychange", listener);
    },
  };
}
