import { useEffect } from "react";

// App-level screen wake lock (issue #981): keep the phone awake on EVERY gated page of the /m app
// while it is foregrounded, not just the Terminal view. It is owned once by the gated layout, so a
// single sentinel is held for the whole session and navigating between pages never creates a second.
//
// A screen wake lock is auto-released by the browser when the tab is hidden, so we re-acquire on
// visibilitychange when the app returns to the foreground and release when it is backgrounded. Where
// the Screen Wake Lock API is unavailable this is a silent no-op (no fallback), exactly as the old
// Terminal-only effect behaved. The [wakeLock] acquired/released logs make the single-sentinel
// behavior observable for QA (issue #981 acceptance).

type WakeLockSentinelLike = {
  release: () => Promise<void>;
  addEventListener?: (type: "release", listener: () => void) => void;
};
type WakeLockNavigator = Navigator & {
  wakeLock?: { request: (type: "screen") => Promise<WakeLockSentinelLike> };
};

export function useScreenWakeLock(): void {
  useEffect(() => {
    const wl = (navigator as WakeLockNavigator).wakeLock;
    if (!wl) return; // unsupported browser: silent no-op, no fallback

    let sentinel: WakeLockSentinelLike | null = null;
    let disposed = false;

    const acquire = async () => {
      if (disposed || sentinel !== null) return; // one sentinel at a time
      if (document.visibilityState !== "visible") return; // only hold it while foregrounded
      try {
        const s = await wl.request("screen");
        if (disposed) {
          void s.release().catch(() => {});
          return;
        }
        sentinel = s;
        console.log("[wakeLock] acquired");
        // The browser can auto-release the lock (e.g. when the tab is hidden); clear our reference so
        // returning to the foreground re-acquires cleanly.
        s.addEventListener?.("release", () => {
          if (sentinel === s) {
            sentinel = null;
            console.log("[wakeLock] released");
          }
        });
      } catch {
        // Denied or transient; retried on the next visibilitychange.
      }
    };

    const release = () => {
      const s = sentinel;
      sentinel = null;
      if (s) {
        console.log("[wakeLock] released");
        void s.release().catch(() => {});
      }
    };

    const onVisibility = () => {
      if (document.visibilityState === "visible") void acquire();
      else release();
    };

    void acquire();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      disposed = true;
      document.removeEventListener("visibilitychange", onVisibility);
      release();
    };
  }, []);
}
