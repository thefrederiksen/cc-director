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
export default defineConfig({
  base: "/c/",
  plugins: [react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
});
