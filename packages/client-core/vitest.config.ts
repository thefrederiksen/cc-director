import { defineConfig } from "vitest/config";

// Most of this library is pure logic and tests fine in node, which is why there was no config here at all.
// The shared REACT HOOKS need a document to render into, so .tsx tests get jsdom and the plain .ts tests
// keep running in node - the cheap environment stays cheap, and a hook can still be tested where it lives
// instead of being tested from whichever app happens to import it.
export default defineConfig({
  test: {
    environment: "node",
    environmentMatchGlobs: [["**/*.test.tsx", "jsdom"]],
  },
});
