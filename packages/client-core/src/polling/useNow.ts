// A live wall-clock time that re-renders once a second, off the ONE shared ticker (issue #1239). A page
// that shows relative-time labels ("last seen 12s ago") reads `const now = useNow()` and passes it to
// its relativeTime formatter, instead of standing up its own setInterval(..., 1000) just to force the
// re-render. All callers share sharedClock, so there is a single 1-second timer for the whole app, and
// it pauses while the tab is hidden.
import { useSyncExternalStore } from "react";
import { sharedClock } from "./clock";

export function useNow(): number {
  return useSyncExternalStore(sharedClock.subscribe, sharedClock.getSnapshot, sharedClock.getSnapshot);
}
