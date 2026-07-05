# @devthrottle/client-core

The one shared TypeScript library both DevThrottle browser shells depend on - the mobile PWA
(`apps/mobile`) today and the React Cockpit (`apps/cockpit`) next. Promoting this logic out of the
mobile app removes the duplication the Cockpit rebuild would otherwise create (Epic #967, Issue #968).

Everything here talks **only to the Gateway** through root-relative URLs. A workspace-wide ESLint rule
bans absolute `http`/`https`/`ws`/`wss` literals so a Director address can never leak into client code
(see `eslint.config.js` at the repository root). The single intended exception - the public
devthrottle.com sign-in site - is documented inline in `src/auth/enrollRequest.ts`.

## What lives here

- `src/api/` - the typed Gateway client (`client.ts`), the AI/enroll/chunking helpers, and the
  `openapi-typescript`-generated `schema.ts`.
- `src/history/` - the bubble mapper, links, markdown, and text helpers (they mirror the Cockpit's C#
  `History*` services).
- `src/terminal/` - the xterm byte-stream engine (stream + reconnect + fit) and key encoding.
- `src/dictation/` - the microphone recorder, chunked upload, and transcript cleanup, plus the
  `DictationDialog` React component.
- `src/auth/` - device-key / cookie handling, sign-in, and enrollment (React components + helpers).
- `src/sessions/` - session ordering and waiting-state derivation.

## Consuming it

The package's `exports` map points at TypeScript **source**, so the shells' bundlers (Vite) and
`tsc` resolve it directly with no separate build step:

```ts
import { listSessions } from "@devthrottle/client-core/api/client";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
```

## Scripts

```bash
npm run typecheck   # tsc --noEmit over src
npm run build       # emit declaration files to dist/ (proves the library compiles on its own)
npm run gen:api     # regenerate src/api/schema.ts from a Gateway's OpenAPI document at 127.0.0.1:7878
```

Because the typed client is compiled against `schema.ts`, a C# DTO change that is not reflected in a
regenerated schema fails the build - the C# DTOs stay the single source of truth.
