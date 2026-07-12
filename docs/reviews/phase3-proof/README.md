# Local Files - Phase 3 proof (mobile /m file viewer)

These screenshots prove the Phase 3 deliverables end to end in the running mobile /m React app:
the full-screen file viewer route, the clickable chat path link, and the mobile terminal (xterm)
link provider. Every file type is opened from BOTH the Chat page and the Terminal mirror, by a REAL
click, and the resulting render is screenshotted.

## What was proven

- The mobile viewer is a full-screen route (`/session/:id/file?path=...`) that renders each type
  per brief decision 4, mirroring the Cockpit `FileViewerModal` render logic (only the shell differs -
  a page, not a modal): image via `<img>`, pdf via `<iframe>` (native viewer), html via a SANDBOXED
  `<iframe sandbox="allow-scripts">` (no `allow-same-origin`), markdown via the shared sanitized
  `markdownToHtml()`, text/code via a `<pre>` with horizontal scroll. Each screen has a Back control
  (browser back to the session) and a Download link.
- The Chat page's inert copy-only file-path span is now a clickable control that navigates to the
  viewer route; the copy button is kept.
- The mobile terminal (`packages/client-core/src/terminal/stream.ts`) has a new xterm link provider
  that REUSES `findLineLinks` (the same helper the Cockpit terminal uses) and routes a FILE-path click
  to the app-supplied `onFileLink(path)` callback, which navigates to the viewer route. http/https
  URLs open in a new tab and never reach the callback.

## Screenshots

Chat clicks (real `.chat-link-view` button clicks):
- `chat-1-image.png` - image (`proof.png`) rendered with `<img>`.
- `chat-2-pdf.png` - pdf (`report.pdf`) in the browser's native PDF viewer (`<iframe>`).
- `chat-3-html.png` - html (`report.html`) in the SANDBOXED iframe; the inline script ran ("inline
  script ran (sandbox allow-scripts)"), proving `allow-scripts` works while the null origin denies
  `allow-same-origin` cookie authority.
- `chat-4-markdown.png` - markdown (`report.md`) rendered with the shared sanitized `markdownToHtml()`.
- `chat-5-text.png` - text/code (`sample.py`) in a monospace `<pre>` (horizontal scroll: the long line
  is clipped at the right edge and pans, not wrapped).

Terminal clicks (real xterm link clicks on the underlined path, new provider in `stream.ts`):
- `term-1-image.png` through `term-5-text.png` - the same five files, opened by clicking the path in
  the live mobile terminal mirror.

## How this was captured (method)

- The mobile app was served by its own vite dev server from an isolated worktree off `origin/main`
  (which carries Phase 1 `GET /sessions/{sid}/file` and Phase 2), and driven with Playwright.
- The data endpoints were provided by an in-page fetch shim (Playwright route interception): the real
  `GET /sessions` roster, `GET /sessions/{sid}/history` (five assistant messages, each naming one
  absolute file path so the shared `extractLinks` turns it into a chat link), and the actual
  `GET /sessions/{sid}/file?path=...` endpoint under test (serving fixture bytes with the right
  content-type). The terminal stream (`/sessions/{sid}/stream`) was a monkeypatched WebSocket that
  emits a size frame and a few lines containing the five paths, exactly as a real PTY would.
- This is the sanctioned no-live-Gateway Phase 3 proof pattern. The RUNNING fleet Gateway/Director
  predate Phase 1, so a real-phone hit against the live front door would 404 on `/sessions/{sid}/file`
  until the Phase 1+ build is deployed to the live Gateway - a mission-level step the Manager owns
  (it restarts the fleet). The live production Gateway was NOT touched.

Status: **code complete + Playwright-proven; real-phone sign-off pending the live-Gateway deploy the
Manager owns.**

## Notes and stated limitations (per brief)

- PDF in an iframe: on some mobile browsers (notably iOS Safari) a PDF may not render inline; that is a
  known mobile-browser limitation to state, not a bug to hack around, and the Download link is the
  fallback. Here it renders in Chromium's native viewer (see `term-2-pdf.png` / `chat-2-pdf.png`).
- HTML that references sibling assets by relative path (a separate .css, an external image) will not
  resolve them, because the viewer loads a single file by query string, not a directory. Self-contained
  HTML (what cc-html emits, inlining CSS/JS/images) renders correctly, and that is the primary case
  (generated reports). Not solved in v1 by design.
- The `download` mode (unknown/binary: filename + Download link) and richer error/permission states are
  Phase 4 polish; unit tests for the stream link provider are Phase 4 as scoped in the brief.
