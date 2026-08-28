import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// CC Assistant is its own installed application, separate from the mobile app, because a Progressive
// Web Application gets exactly one manifest and one home screen icon. The phone in the kitchen
// charger has to show this, not the fleet cockpit.
// WHERE THE APP IS MOUNTED. Two homes, one build. On its own static host it sits at the root of a
// domain; on the Gateway it will sit under /cc-assistant/ beside /mobile and /c. Everything that
// needs the path reads it from here or from import.meta.env.BASE_URL, so the app never hard-codes
// where it lives. Set APP_BASE at build time to move it.
const appBase = process.env.APP_BASE ?? "/cc-assistant/";

export default defineConfig({
  base: appBase,
  server: {
    port: 5183,
    // Listen on the network as well as loopback, so a phone can reach it. See the README: the phone
    // ALSO needs the page served over a secure connection or the browser refuses the microphone.
    host: true,
  },
  worker: {
    // The speech worker is an ES module because it imports the transformers library.
    format: "es",
  },
  optimizeDeps: {
    // Pre-bundling this rewrites the paths it uses to fetch its own WebAssembly and model files.
    exclude: ["@huggingface/transformers"],
  },
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      manifest: {
        name: "Wilson",
        short_name: "Wilson",
        description: "A voice assistant for the kitchen. Say its name and talk to it.",
        start_url: appBase,
        scope: appBase,
        display: "standalone",
        orientation: "portrait",
        background_color: "#0f1216",
        theme_color: "#0f1216",
        icons: [],
      },
      workbox: {
        // NETWORK FIRST FOR THE SHELL AND THE BUNDLE. A precached, cache-first index.html serves a
        // STALE bundle for a whole load even when the server has a fresh one, so a deploy never
        // reaches an already-installed app. That is not a theoretical risk: it is what happened here
        // on 28 August, and it meant two verification runs were reading code that was no longer the
        // code being served. The mobile app in this repository hit the same thing and its config
        // documents it at length.
        //
        // This app is under daily change, so freshness beats offline caching. Only icons and fonts
        // are precached; the shell and the hashed bundle go through the network-first rules below,
        // and fall back to the last copy only when there is genuinely no network.
        skipWaiting: true,
        clientsClaim: true,
        cleanupOutdatedCaches: true,
        globPatterns: ["**/*.{svg,png,ico,woff2}"],
        maximumFileSizeToCacheInBytes: 8 * 1024 * 1024,
        runtimeCaching: [
          {
            urlPattern: ({ request }: { request: Request }) => request.mode === "navigate",
            handler: "NetworkFirst",
            options: {
              cacheName: "assistant-shell",
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 4, maxAgeSeconds: 60 * 60 * 24 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
          {
            urlPattern: ({ url }: { url: URL }) => url.pathname.startsWith("/assets/"),
            handler: "NetworkFirst",
            options: {
              cacheName: "assistant-assets",
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 60, maxAgeSeconds: 60 * 60 * 24 * 7 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
        ],
      },
    }),
  ],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
});
