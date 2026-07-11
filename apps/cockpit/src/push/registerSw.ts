// Registers the Cockpit's push service worker (issue #1257). The mobile app gets its service worker
// auto-registered by vite-plugin-pwa; the Cockpit is a plain static single-page app with no PWA plugin,
// so it registers its one hand-written worker (public/sw.js, served at "/sw.js" so its scope is the
// whole Cockpit) here at startup. The worker only carries the Web Push listeners - it precaches nothing.
//
// Registration is idempotent (the browser reuses an existing registration and updates the script when
// it changes) and non-fatal: a browser without service worker support, or a failed registration, simply
// leaves the Cockpit without desktop notifications - every other feature works unchanged.

export async function registerCockpitServiceWorker(): Promise<void> {
  if (typeof navigator === "undefined" || !("serviceWorker" in navigator)) return;
  try {
    await navigator.serviceWorker.register("/sw.js");
  } catch (err) {
    console.warn("[push] Cockpit service worker registration failed:", err);
  }
}
