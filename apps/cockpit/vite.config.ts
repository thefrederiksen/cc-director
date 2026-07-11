import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The desktop Cockpit is the Gateway's canonical front door: it is served at the site root "/"
// (epic #967 cutover, issue #979 - the Blazor Server Cockpit is retired). Every asset URL is
// root-relative. Build output goes to dist/, which the Gateway's release-gated MSBuild target
// (BuildCockpitApp on CcDirector.Gateway.csproj) copies into wwwroot/c/; the Gateway serves those
// files at "/" and falls unknown page paths back to index.html for the React router.
//
// No service worker / PWA here: the desktop Cockpit is not an offline-installable app the way the
// phone shell is. It is a plain static single-page app the Gateway serves same-origin.
// Dev-only: when COCKPIT_PROXY_TARGET is set (e.g. http://127.0.0.1:7878 for a live Gateway, or a
// test Director's Control API port), the dev server fronts that origin for the Gateway routes the
// client calls root-relative - so `npm run dev` on this app drives a real fleet without a production
// build. The terminal WebSocket (/sessions/{sid}/stream) needs ws:true. This affects ONLY the dev
// server; the production build (served by the Gateway at /c) never uses it. The target is read from
// the environment, never a hard-coded address, so the Gateway-only-ingress rule is not weakened.
const proxyTarget = process.env.COCKPIT_PROXY_TARGET;
// Every root-relative prefix any Cockpit page calls must be fronted, or that page 404s under
// `npm run dev`. The list below is the union of the prefixes the pages fetch (client-core + the
// views): the session/fleet core plus every later-ported page - Lists (/lists), Exes (/exes),
// Account (/account), the settings + telemetry + about pages (/gateway), Dictionary + recordings
// (/ingest, /dictation), Feedback (/turnbriefs), and the settings AI-key panel (/vault).
const devProxy = proxyTarget
  ? {
      "/sessions": { target: proxyTarget, changeOrigin: true, ws: true },
      "/directors": { target: proxyTarget, changeOrigin: true },
      "/interrupted": { target: proxyTarget, changeOrigin: true },
      "/fanout": { target: proxyTarget, changeOrigin: true },
      "/cron": { target: proxyTarget, changeOrigin: true },
      "/wingman": { target: proxyTarget, changeOrigin: true },
      "/lists": { target: proxyTarget, changeOrigin: true },
      "/exes": { target: proxyTarget, changeOrigin: true },
      "/account": { target: proxyTarget, changeOrigin: true },
      "/gateway": { target: proxyTarget, changeOrigin: true },
      "/ingest": { target: proxyTarget, changeOrigin: true },
      "/dictation": { target: proxyTarget, changeOrigin: true },
      "/turnbriefs": { target: proxyTarget, changeOrigin: true },
      "/vault": { target: proxyTarget, changeOrigin: true },
      // Web Push (issue #1257): the notifications toggle fetches the VAPID public key and registers /
      // unregisters this browser's subscription at the Gateway's /push/* endpoints, so `npm run dev`
      // reaches them against a live Gateway too.
      "/push": { target: proxyTarget, changeOrigin: true },
      // Device enrollment (issue #1088): the shared client-core callback exchanges the cloud device
      // key at the Gateway's POST /m/enroll, so the enrollment flow works under `npm run dev` too.
      "/m": { target: proxyTarget, changeOrigin: true },
    }
  : undefined;

export default defineConfig({
  base: "/",
  plugins: [react()],
  server: {
    proxy: devProxy,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
});
