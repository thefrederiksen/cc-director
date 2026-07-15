import { useEffect } from "react";

// THE one place the mobile app learns how tall the screen actually is.
//
// Why this exists at all: on the owner's Android PWA, CSS `100dvh` DOES NOT track the visible
// height. The box stays taller than the screen, so the page gains a sliver of scroll, the top row
// slides away under a swipe, and controls pinned to the bottom render below the browser toolbar.
// This is not theoretical and it is not new - it has been re-fixed repeatedly:
//
//   #1058  session screens: swap vh -> dvh
//   #1349  voice speaking state: fit the viewport
//   #1351  Terminal + Chat: overflow-clip shell, no page scroll
//   #1404  Car Mode v4: primary button cut off
//   #1408  Car Mode v5: dvh abandoned - pin to window.visualViewport.height instead
//
// #1408 is the only one that actually held on the real device, because it stopped trusting `dvh`.
// This hook is that fix, promoted out of Car Mode so EVERY screen shares one copy. Mounted once in
// the app shell (main.tsx GatedLayout), it publishes the true visible height as `--app-vh`, and the
// stylesheet sizes .terminal-screen and .car-screen from it. Do not re-implement it per page: the
// reason this bug keeps coming back is that each screen has been solving it privately.
//
// window.visualViewport.height is the ACTUALLY visible height - it excludes the browser toolbars and
// the on-screen keyboard, which is exactly what `dvh` gets wrong here. The `dvh`/`vh` fallbacks in
// the stylesheet cover the first paint and any browser without visualViewport.
//
// Guarded by MobileViewportContractTests (C#), which runs in CI, because the JavaScript has no test
// step there and a guard nobody runs is not a guard.

/** Pick the true visible height. Exported for the unit test; takes its inputs rather than reading
 *  globals so it can be tested without a DOM. */
export function pickVisibleHeight(visualViewportHeight: number | null, innerHeight: number): number {
  // visualViewport is the truth when present. A zero/negative reading is nonsense (some browsers
  // report 0 mid-rotation) - fall back rather than collapse the screen to nothing.
  if (visualViewportHeight !== null && visualViewportHeight > 0) return Math.round(visualViewportHeight);
  return Math.round(innerHeight);
}

export function useVisibleViewportHeight(): void {
  useEffect(() => {
    const root = document.documentElement;
    const apply = () => {
      const vv = window.visualViewport;
      const height = pickVisibleHeight(vv !== null && vv !== undefined ? vv.height : null, window.innerHeight);
      root.style.setProperty("--app-vh", `${height}px`);
    };
    apply();

    const vv = window.visualViewport;
    // Re-fit as the toolbars slide away, the keyboard opens, or the phone rotates.
    vv?.addEventListener("resize", apply);
    vv?.addEventListener("scroll", apply);
    window.addEventListener("resize", apply);
    window.addEventListener("orientationchange", apply);
    return () => {
      vv?.removeEventListener("resize", apply);
      vv?.removeEventListener("scroll", apply);
      window.removeEventListener("resize", apply);
      window.removeEventListener("orientationchange", apply);
      root.style.removeProperty("--app-vh");
    };
  }, []);
}
