# DevThrottle Mobile

A React + TypeScript Progressive Web App, served by the Gateway at `/mobile`. It is a thin **shell** over
the shared library `@devthrottle/client-core` (`packages/client-core`): every shared concern - the
typed Gateway client, history rendering, the terminal byte-stream engine, dictation, device auth, and
session ordering - lives in the package, and this app owns only its screens, routing, and styles.

This app is one member of the npm **workspace** rooted at the repository root (`packages/*` and
`apps/*`). Install dependencies once at the workspace root, not here.

## Build

```bash
# From the repository root (the workspace root):
npm ci
npm run build --workspace @devthrottle/mobile   # type-checks, then builds the static app into dist/
```

`dist/` is static files only. There is no runtime Node dependency: the Gateway serves the built
files, and the release pipeline (the `BuildMobileApp` MSBuild target on `CcDirector.Gateway.csproj`,
gated to a publish/release configuration) runs `npm ci` at the workspace root and
`npm run build --workspace @devthrottle/mobile`, then copies this app's `dist/**` into the Gateway's
`wwwroot/mobile/`. A routine `dotnet build` does NOT run npm.

Vite resolves `@devthrottle/client-core` from its TypeScript source (the package's `exports` map
points at `src/`), so there is no separate library build step to sequence.

## Typed API client and the DTO guarantee

The typed Gateway client and its generated types now live in the package. Regenerate the types there:

```bash
# With a Gateway running on 127.0.0.1:7878:
npm run gen:api --workspace @devthrottle/client-core   # or, from the root: npm run gen:api
```

A C# DTO change that is not reflected in a regenerated `packages/client-core/src/api/schema.ts` fails
the TypeScript build (the typed client stops compiling), so the C# DTOs stay the single source of
truth.

## Gateway-only ingress

The browser talks **only** to the Gateway, through root-relative paths. An ESLint rule
(`eslint.config.js` at the workspace root) bans absolute `http`, `https`, `ws`, and `wss` string
literals in `packages/**` and `apps/**` so a Director address can never leak to the client. Run it
with `npm run lint` at the root.

## Local dev

```bash
$env:MOBILE_PROXY_TARGET = "https://gateway.devthrottle.com" # PowerShell example
npm run dev --workspace @devthrottle/mobile
```

`MOBILE_PROXY_TARGET` is opt-in. When set, Vite forwards the Gateway API prefixes (including the
exact `/mobile/enroll` endpoint) while continuing to serve the app itself under `/mobile`.

## Layout

- `src/pages/` - the screens (Home roster, Terminal, Chat, VoiceMode, NewSession, Settings).
  Settings is only a frame: its tabs and cards are the SAME components the Cockpit renders, from
  `@devthrottle/client-core/settings`, so the two surfaces cannot offer different settings.
- `src/components/` - shared on-screen controls (nav drawer, session controls, view tabs).
- `src/push/`, `src/voice/` - the app-shell-only push registration and voice clip helpers.
- Everything shared with the React Cockpit lives in `@devthrottle/client-core`.
