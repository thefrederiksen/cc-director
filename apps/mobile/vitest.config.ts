import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Vitest-only configuration. It exists so the test runner does NOT load vite.config.ts: that config
// runs the PWA service-worker plugin and shells out to git for the build stamp, neither of which has
// any business in a unit-test run. Tests declare their own environment per file (the
// @vitest-environment pragma), exactly like the cockpit workspace's tests.
export default defineConfig({
  plugins: [react()],
});
