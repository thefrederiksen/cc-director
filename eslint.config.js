// Gateway-only-ingress lint rule (Epic #967 / Issue #968).
//
// The hard rule the Cockpit rebuild rests on: the browser talks ONLY to the Gateway, never to a
// Director directly. Every request the client makes is a root-relative path; a Director address can
// only reach the browser as an absolute URL. So we ban absolute "http://", "https://", "ws://", and
// "wss://" string literals in the shared library and every shell. This makes the regression
// impossible to commit rather than something a reviewer has to catch by eye.
//
// The config is deliberately minimal: it wires the TypeScript parser and this one rule, and does NOT
// pull in a broad recommended rule set, so "the whole workspace passes" means exactly "no absolute
// Director URL leaked into client code" and nothing unrelated.
//
// The one intended exception is the sign-in flow, which points the browser at the PUBLIC
// devthrottle.com website (not a Director) to obtain a device key. That single site literal carries
// an inline eslint-disable with a documented reason (packages/client-core/src/auth/enrollRequest.ts).

import tseslint from "typescript-eslint";

const GATEWAY_ONLY_MESSAGE =
  "Gateway-only-ingress (#967): the browser must talk only to the Gateway through root-relative " +
  "paths - no absolute http/https/ws/wss URL in client code. A Director address can only reach the " +
  "client as an absolute URL, so this literal would be exactly that regression. Use a root-relative " +
  "path. The one intended exception (the public devthrottle.com sign-in site) is documented inline " +
  "in packages/client-core/src/auth/enrollRequest.ts.";

const bannedAbsoluteUrl = [
  // Plain string literals, e.g. "https://some-director" or 'ws://...'.
  {
    selector: "Literal[value=/^(https?|wss?):\\/\\//i]",
    message: GATEWAY_ONLY_MESSAGE,
  },
  // Template literals whose first chunk pins an absolute scheme, e.g. `wss://${host}/stream`.
  {
    selector: "TemplateElement[value.cooked=/^(https?|wss?):\\/\\//i]",
    message: GATEWAY_ONLY_MESSAGE,
  },
];

export default [
  {
    // Generated, vendored, and build-tool config files are not client application code.
    ignores: [
      "**/node_modules/**",
      "**/dist/**",
      "**/*.config.ts",
      "**/*.config.js",
      "**/*.config.mjs",
      "**/*.config.cjs",
    ],
  },
  {
    files: ["packages/**/*.{ts,tsx}", "apps/**/*.{ts,tsx}"],
    languageOptions: {
      parser: tseslint.parser,
      parserOptions: {
        ecmaVersion: "latest",
        sourceType: "module",
        ecmaFeatures: { jsx: true },
      },
    },
    rules: {
      "no-restricted-syntax": ["error", ...bannedAbsoluteUrl],
    },
  },
  {
    // Unit tests are not shipped client code - they never run in the browser and make no real
    // request. Their fixtures legitimately contain absolute-URL string literals as TEST DATA (e.g.
    // the link recognizer must be exercised with "https://example.com"), so the Gateway-only-ingress
    // ban does not apply to them.
    files: ["**/*.test.{ts,tsx}"],
    rules: {
      "no-restricted-syntax": "off",
    },
  },
];
