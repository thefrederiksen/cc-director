# Mission Brief: Local Files (remote file viewer)

Status: active mission. Written 2026-07-11 by the Architect session ("Local Files - Architect",
session c5c299d0, machine SOREN_NORTH). This document is the Architect's handover to the Manager
session. The Manager owns execution from here; the Architect does not gate the Manager.

## The mission

When a local file path appears in a session's terminal output or in its chat - an image, an
HTML page, a Markdown file, a PDF, a text or code file - the remote clients (the cockpit React
app and the mobile phone app at /m) must show it as a clickable link, and clicking it must open
that file in a viewer, rendered in place, streamed from the machine the session runs on. Today
those paths are dead text: on mobile and cockpit chat a detected file path renders as an inert
"copy path" label, and in the terminal nothing is clickable at all. A person driving a session
from their phone or from the cockpit cannot see the picture, the report, or the document the
agent just produced without walking over to the machine. This mission closes that gap.

The shape is: the path is on a specific machine; the client never talks to that machine
directly; the request rides the Gateway to the owning session's Director, which reads the bytes
off its own disk and streams them back. This is the same path the screenshot viewer already
uses - we are generalizing it from "one screenshot image" to "any local file, rendered by type."

## The core finding - most of this already exists

The Architect verified the following against the working tree on 2026-07-11. Read it before
starting; it is why this is a small mission, not a large one.

1. The Director ALREADY serves local files over HTTP. `GET /file?path=<absolute path>` at
   `src/CcDirector.ControlApi/ControlEndpoints.cs:1010-1036` streams any local file INLINE
   with a correct content-type already mapped for html, pdf, png, jpg/jpeg, gif, svg, css, js,
   json, csv, md, txt, log, and an octet-stream fallback. A code comment on it (lines 1002-1009)
   calls it the "mobile view-links" feature and explicitly notes it has no sandbox - the tailnet
   is the only gate. Someone started this feature; it is half-built. The two gaps: it is NOT
   reachable through the Gateway (only loopback-direct or that machine's own Tailscale Serve
   front door), and it is a top-level route, not session-scoped, so the Gateway's per-session
   proxy does not forward it.

2. The exact remote request path we need is already proven by the screenshot-bytes viewer.
   Client calls the Gateway same-origin at `GET /sessions/{sid}/screenshots/file`; the Gateway
   resolves the session's owning Director and streams the bytes back unchanged. Blueprint files:
   `src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs` (the proxy legs and the generic
   per-session catch-all `app.Map("/sessions/{sid}/{**rest}")` at line 101 that forwards any
   remaining verb to the owning Director), and `screenshotFileUrl()` in
   `packages/client-core/src/api/client.ts:595`. The Director side is
   `GET /screenshots/file` at `ControlEndpoints.cs:2642` using
   `Results.File(full, contentType, enableRangeProcessing: true)`.

3. Link DETECTION already exists and is already wired into CHAT on both apps.
   `extractLinks(body)` in `packages/client-core/src/history/historyLinks.ts:48` detects
   absolute Windows paths (`C:\...`, `C:/...`), `/c/...` Unix-style paths, http/https URLs, and
   `file://` URLs (converted to a local path). It is a TS port of
   `src/CcDirector.Core/Utilities/LinkDetector.cs`. Chat already renders the detected links: the
   cockpit chat at `apps/cockpit/src/sessions/ChatTab.tsx:92-116` and the mobile chat at
   `apps/mobile/src/pages/Chat.tsx:139-163` both split each link into a real anchor for URLs and
   an INERT copy-only span for file paths. That inert span is the exact spot the viewer plugs
   into. The terminal (cockpit `packages/client-core/src/terminal/interactive.ts`, mobile
   `packages/client-core/src/terminal/stream.ts`) has NO link handling at all - that is new work.

4. Markdown rendering already exists. `marked` is a dependency
   (`packages/client-core/package.json`) and `markdownToHtml(text)` in
   `packages/client-core/src/history/historyMarkdown.ts:71` is a sanitized, XSS-safe renderer
   (GFM on, raw HTML escaped, href scheme allow-list) that both chat views already trust. The
   Markdown viewer reuses it directly.

5. There is NO viewer / lightbox / modal file component today. The cockpit screenshots panel
   just opens raw bytes in a new browser tab. The reusable cockpit modal shell is
   `apps/cockpit/src/components/ConfirmDialog.tsx` (the `ui-modal-backdrop` convention). Mobile
   has no modal component and uses full routes/pages (`apps/mobile/src/components/ViewTabs.tsx`).
   The viewer is built from scratch in each app.

## The request path we are building

Client viewer (cockpit or /m) issues a same-origin GET to the Gateway:
`GET /sessions/{sid}/file?path=<url-encoded absolute path>`.
-> The global Gateway auth middleware admits it on the caller's enrolled per-device key
   (`AuthMiddleware.HasValidToken(ctx, Token, Devices)`; browser `<img>`/`<iframe>` src carry
   the `cc-gateway-token` cookie exactly as screenshots do).
-> The Gateway's per-session catch-all proxy resolves the session's owning Director and forwards
   the request, presenting the shared fleet Bearer token, to that Director at the SAME path.
-> The Director reads the file off ITS OWN disk and streams the bytes back with the right
   content-type and range support. Because the session lives on that machine, an absolute path
   emitted by that session's terminal or chat is always a path on that same machine - correct by
   construction, no machine-selection needed.

The client is viewing a specific session, so it always has the session id; session-scoping is
purely the routing vehicle that reaches the right machine and reuses all existing auth and
proxy plumbing. It is NOT a sandbox - see decision 2.

## Decisions already made - do not re-litigate

1. Reuse the screenshot request path, session-scoped. Add `GET /sessions/{sid}/file` on the
   Director (a session-scoped sibling of the existing top-level `/file`), so the Gateway's
   existing `/sessions/{sid}/{**rest}` catch-all forwards it for free. Do NOT add new Gateway
   proxy plumbing and do NOT try to reach the top-level `/file` from the Gateway.

2. NO path sandbox. Any absolute path the caller asks for is served if it exists (404 if not).
   This is a settled product decision by the owner (2026-07-11): the trust boundary is Tailscale
   network membership plus an enrolled device key. Anyone who is on the tailnet and holds a valid
   device key is already trusted with the machine. Do not add allowed-roots, repo-anchoring, or
   proof-of-mention checks. This matches the existing `/file` behavior.

3. One exception to "we harden nothing," and it is mandatory: the HTML viewer must render the
   file in a SANDBOXED iframe (a `sandbox` attribute WITHOUT `allow-same-origin`). The file is
   served from the Gateway origin, so an HTML file containing script would otherwise run with the
   Gateway's ambient cookie authority and could call Gateway APIs as the user. A sandboxed iframe
   runs the page in a null origin, which removes that authority. This is a safety property of the
   viewer, not a restriction on what files can be read. Additionally set
   `X-Content-Type-Options: nosniff` on the file response. Images, PDF, Markdown, and text do not
   execute and do not need the sandbox, but the HTML case does.

4. Viewer type is decided client-side by file extension, and the client picks the render mode:
   - image (png, jpg, jpeg, gif, svg, webp, bmp): `<img src=fileUrl>`
   - pdf: `<iframe src=fileUrl>` (browser native PDF viewer)
   - html, htm: sandboxed `<iframe src=fileUrl>` (decision 3)
   - md, markdown: fetch the text, render with `markdownToHtml()`
   - text/code (txt, log, json, csv, and any other textual extension): fetch the text, `<pre>`
   - anything else / unknown / binary: do not guess - show the file name, size, and a Download
     button (a plain link to `fileUrl` with a download attribute). Fail loud, no fallback render.

5. Detection reuses `extractLinks()` unchanged for what it already finds; extend it (or a thin
   wrapper) only to classify a detected path into one of the viewer types above by extension, so
   the click knows which mode to open. Do not rewrite the detector or add relative-path guessing
   (it deliberately avoids relative paths to prevent false positives).

6. The viewer lives in each app; client-core stays app-agnostic. The terminal link provider in
   client-core must not import app UI. It takes an `onFileLink(path)` callback the app supplies;
   the app's handler opens that app's viewer. Same for chat: the render lives in the app.

7. Plain English everywhere, ASCII only in code and output. No fallback programming: a missing
   file is a loud 404 with a clear message in the viewer, never a silent blank or a guess.

8. Windows first: build and human-verify every phase on SOREN_NORTH driving the real Gateway and
   a real phone. The Mac gets a verification pass at the end (one Avalonia/React codebase, no
   porting step; #125 already tracks embedded-WebView file viewing on macOS - link it, do not
   solve it here).

## Known limitation to state, not solve, in v1

HTML files that reference sibling assets by relative path (a separate .css, an external image)
will not resolve those assets, because the viewer loads a single file by query string, not a
directory. Self-contained HTML - which is what the repo's own document tooling (cc-html) emits,
inlining CSS/JS/images - renders correctly, and that is the primary case (generated reports).
Do not build a directory-serving or asset-rewriting proxy in this mission. If it proves needed,
it is a follow-up. State the limitation in the Phase 4 report.

## The work, in phases

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to a real
machine, and clickable by the owner before the next phase begins.

- Phase 1 - Server and API foundation. Add `GET /sessions/{sid}/file` on the Director
  (`ControlEndpoints.cs`), reusing the existing `/file` content-type map and adding
  `enableRangeProcessing: true` and `X-Content-Type-Options: nosniff`. Confirm the Gateway's
  per-session catch-all forwards it (no Gateway code should be needed; verify, do not assume).
  Add `sessionFileUrl(sid, path)` and `fetchSessionFileText(sid, path)` to
  `packages/client-core/src/api/client.ts` next to `screenshotFileUrl`. Add a small
  `classifyFile(path)` helper (extension -> viewer type) in client-core.
  Proof: a real remote GET through the Gateway returns an image's bytes and a text file's bytes
  from another machine's session; a round-trip test modeled on
  `src/CcDirector.Gateway.Tests/ScreenshotProxyRoundTripTests.cs`.

- Phase 2 - Cockpit viewer, chat links, terminal links. Build the cockpit `FileViewerModal`
  (modeled on `ConfirmDialog`) that renders each type per decision 4. Turn the inert chat path
  span (`ChatTab.tsx`) into a clickable control that opens the viewer (keep the copy button).
  Add an xterm link provider in `packages/client-core/src/terminal/interactive.ts` that runs
  `extractLinks` over rendered line text and calls the app's `onFileLink` handler on click.
  Proof: from the cockpit, click a file path in chat AND in the terminal for an image, a PDF, a
  Markdown file, an HTML report, and a text file; screenshots of each rendering.

- Phase 3 - Mobile (/m) viewer, chat links, terminal links. Build the mobile viewer as a
  full-screen route (per `ViewTabs` conventions); wire the mobile chat path links
  (`apps/mobile/src/pages/Chat.tsx`) and a terminal link provider in
  `packages/client-core/src/terminal/stream.ts`. Reuse the client-core classify/URL helpers and
  the same viewer logic; only the shell differs.
  Proof: on the real phone at /m, open each of the five file types from chat and from the
  terminal; screenshots from the phone.

- Phase 4 - Hardening and polish. Download button for unknown/binary types; loud, specific
  error and permission states (file not found, session offline -> the Gateway's 503); large-file
  and range behavior for big PDFs/images; the HTML relative-asset limitation written into the
  report; unit tests for `classifyFile` and the terminal link provider; the macOS verification
  pass (link #125). No new capability - correctness, tests, and the edges.

## Definition of done for the mission

1. All four phases merged to origin/main, each human-verified on this Windows machine against the
   real Gateway, and Phase 3 verified on the real phone.
2. From the cockpit AND from the phone, a file path in the terminal and in the chat is clickable
   and opens the file rendered by type - image, PDF, HTML, Markdown, and text all proven.
3. No path sandbox exists (decision 2); the HTML viewer is sandboxed (decision 3); a missing file
   fails loud with a clear message.
4. Round-trip and unit tests exist and pass; the screenshot round-trip test is the model.
5. A final verification report (HTML, in docs/reviews/) showing every file type opened from both
   clients, from both the terminal and chat, with screenshots from the running cockpit and the
   real phone. The HTML relative-asset limitation is stated in it.
