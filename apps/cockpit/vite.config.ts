import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The desktop Cockpit is served by the Gateway under /c (the direct analog of how the mobile app
// is served under /m), so every asset URL must be /c-rooted. Build output goes to dist/, which the
// Gateway's release-gated MSBuild target (BuildCockpitApp on CcDirector.Gateway.csproj) copies into
// wwwroot/c/. This coexists with the live Blazor Cockpit at "/" during the epic #967 migration, so a
// path can flip from Blazor to React one at a time.
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
const devProxy = proxyTarget
  ? {
      "/sessions": { target: proxyTarget, changeOrigin: true, ws: true },
      "/directors": { target: proxyTarget, changeOrigin: true },
      "/fanout": { target: proxyTarget, changeOrigin: true },
    }
  : undefined;

export default defineConfig({
  base: "/c/",
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
