# Local Files mission - verification report (DRAFT)

Status: DRAFT scaffold, written by the Phase 4 worker on 2026-07-11. The code, tests, and the
screenshot evidence gathered during Phases 2 and 3 are in place. The one remaining section -
real-machine and real-phone sign-off against the LIVE Gateway - is marked PENDING below and is a
Manager-owned closeout step gated on the deployment decision. Do not treat this document as the final
sign-off until that section is filled in.

## The mission, in one paragraph

When a local file path appears in a session's terminal output or its chat - an image, a PDF, an HTML
page, a Markdown file, a text or code file - the remote clients (the cockpit React app and the mobile
phone app at `/m`) show it as a clickable link, and clicking it opens that file rendered in place,
streamed from the machine the session runs on. The request rides the Gateway to the owning session's
Director, which reads the bytes off its own disk and streams them back - the same path the screenshot
viewer already uses, generalized from "one screenshot image" to "any local file, rendered by type."
Full brief: `docs/architecture/local-files-mission-2026-07-11.md`.

## How the request flows (proven in Phase 1)

Client viewer issues a same-origin `GET /sessions/{sid}/file?path=<url-encoded absolute path>` to the
Gateway. The Gateway's per-session catch-all proxy resolves the owning Director and forwards the
request unchanged; the Director reads the file off its own disk and streams the bytes back with the
right content type, range support, and `X-Content-Type-Options: nosniff`. The session id is purely the
routing vehicle that reaches the right machine (the path is always a path on that machine) - it is NOT
a sandbox (brief decision 2). HTML is rendered in a sandboxed iframe without `allow-same-origin` so a
report's own script runs in a null origin with none of the Gateway's cookie authority (brief decision
3).

## Evidence by client x surface x file type

Every file type was opened from BOTH the chat and the terminal, on BOTH clients, during Phases 2 and 3.
The screenshots were captured against a fresh slot-5 Director built from the mission branch (the live
fleet Gateway/Director still predate Phase 1; deploying to the live Gateway is the closeout step). See
each proof folder's `README.md` for the exact capture method.

### Cockpit (React desktop) - Phase 2, `docs/reviews/phase2-proof/`

| File type | Chat click | Terminal click |
|-----------|------------|----------------|
| Image (`proof.png`) | [chat-1-image.png](phase2-proof/chat-1-image.png) | [term-1-image.png](phase2-proof/term-1-image.png) |
| PDF (`report.pdf`) | [chat-2-pdf.png](phase2-proof/chat-2-pdf.png) | [term-2-pdf.png](phase2-proof/term-2-pdf.png) |
| HTML (`report.html`, self-contained cc-html) | [chat-3-html.png](phase2-proof/chat-3-html.png) | [term-3-html.png](phase2-proof/term-3-html.png) |
| Markdown (`report.md`) | [chat-4-markdown.png](phase2-proof/chat-4-markdown.png) | [term-4-markdown.png](phase2-proof/term-4-markdown.png) |
| Text / code (`sample.py`) | [chat-5-text.png](phase2-proof/chat-5-text.png) | [term-5-text.png](phase2-proof/term-5-text.png) |

### Mobile (`/m` PWA) - Phase 3, `docs/reviews/phase3-proof/`

| File type | Chat click | Terminal click |
|-----------|------------|----------------|
| Image | [chat-1-image.png](phase3-proof/chat-1-image.png) | [term-1-image.png](phase3-proof/term-1-image.png) |
| PDF | [chat-2-pdf.png](phase3-proof/chat-2-pdf.png) | [term-2-pdf.png](phase3-proof/term-2-pdf.png) |
| HTML | [chat-3-html.png](phase3-proof/chat-3-html.png) | [term-3-html.png](phase3-proof/term-3-html.png) |
| Markdown | [chat-4-markdown.png](phase3-proof/chat-4-markdown.png) | [term-4-markdown.png](phase3-proof/term-4-markdown.png) |
| Text / code | [chat-5-text.png](phase3-proof/chat-5-text.png) | [term-5-text.png](phase3-proof/term-5-text.png) |

## Phase 4 hardening (this slice)

- **Download / unknown-binary polish.** The `download` render mode (unknown or binary extensions -
  no guessed render, per brief decision 4) now shows the file NAME, its human-readable SIZE, and a
  Download button, in both viewers (`apps/cockpit/src/components/FileViewerModal.tsx`,
  `apps/mobile/src/pages/FileView.tsx`). The size is read WITHOUT downloading the file: a one-byte
  ranged request (`Range: bytes=0-0`) makes the Director answer `206 Partial Content` with a
  `Content-Range: bytes 0-0/<total>` header, and the total is the full size
  (`fetchSessionFileSize` in `packages/client-core/src/api/client.ts`; `formatFileSize` in
  `packages/client-core/src/history/fileTypes.ts`). This reuses the range support the endpoint already
  has - no HEAD verb and no new Gateway route were needed. If the size cannot be read, the panel shows
  the name and Download with NO guessed size (the brief's "no fake size" rule).

- **Loud, specific error and permission states.** Both viewers surface distinct, plain-English
  messages, never a blank or a hung spinner: a missing file shows "not found (404)"; an offline owning
  machine shows "the session's machine is offline (503)" (the Gateway returns 503 when the owning
  Director is unreachable, surfaced as a `GatewayError`, the same contract `getScreenshots` uses); an
  image that fails to load shows a specific message instead of a broken-image icon. The download
  panel's size probe now participates in the same contract: a 404/503 during the probe shows the
  specific reason rather than offering a Download that would only fail.

- **Large-file / range behavior.** The Director serves `/sessions/{sid}/file` with
  `enableRangeProcessing: true`, so a big PDF or image can seek and resume. Phase 4 found and closed a
  real gap on the way to proving this: the Gateway's HTTP forwarder did not carry the client's `Range`
  / `If-Range` request header across to the Director, so a ranged request always came back as a full
  `200`. The forwarder now forwards those two conditional headers
  (`src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs`), and the round-trip test proves a Range
  request returns `206 Partial Content` with the exact slice AND the correct total in `Content-Range`,
  through the Gateway proxy (`src/CcDirector.Gateway.Tests/SessionFileProxyRoundTripTests.cs`).

## Known limitation (stated, not solved in v1)

HTML files that reference sibling assets by relative path - a separate `.css`, an external image -
will NOT resolve those assets, because the viewer loads a single file by query string, not a directory.
Self-contained HTML, which is what the repo's own document tooling (`cc-html`) emits by inlining
CSS/JS/images, renders correctly, and that is the primary case (generated reports). Building a
directory-serving or asset-rewriting proxy is deliberately out of scope for v1; if it proves needed it
is a follow-up. The Phase 2/3 HTML screenshots use self-contained cc-html output, which renders fully.

## Tests

All of the following pass on the mission branch (numbers recorded by the Phase 4 worker on 2026-07-11):

- `packages/client-core` (vitest): 349 tests across 32 files pass. This includes the thorough
  `classifyFile` coverage (every viewer type, case-insensitive extensions and basenames, extensionless
  textual basenames, unknown -> download), `formatFileSize`, `fetchSessionFileSize` (Range header sent,
  size read from `Content-Range`, 404/503 -> `GatewayError`), and the terminal link-provider routing
  tests for BOTH engines (`interactive.test.ts` for the cockpit, `stream.test.ts` for mobile): a
  detected FILE path click calls `onFileLink(path)`, an http/https URL does NOT (it opens as a URL),
  and the xterm column ranges line up with the on-screen text.
- `apps/cockpit` (vitest): 81 tests across 7 files pass. Both apps typecheck clean (`tsc --noEmit`)
  and build clean (`vite build`).
- `src/CcDirector.Gateway.Tests` (.NET): 1802 tests pass, including the extended
  `SessionFileProxyRoundTripTests` (image + text byte-identity, `nosniff` survives the proxy, missing
  file 404, the new `206` slice test, and the one-byte size-probe test).

## macOS

The macOS verification pass (embedded-WebView file viewing) is tracked in issue #125 and is handled in
mission closeout, not here. One React/Avalonia codebase, no porting step; #125 is the place that pass
is recorded.

## PENDING: real-machine + real-phone sign-off against the LIVE Gateway (post-deploy)

This section is intentionally left for the Manager to fill in AFTER the deployment decision. It must
record, against the LIVE Gateway (not a slot-5 dev build):

- [ ] Each of the five file types opened from the cockpit chat and cockpit terminal on this Windows
      machine, with fresh screenshots.
- [ ] Each of the five file types opened from the phone `/m` chat and phone terminal, with screenshots
      taken from the real phone.
- [ ] The download/unknown-binary panel showing a real file name + size + working Download.
- [ ] A deliberately-missing file showing the "not found (404)" message, and (if reproducible) an
      offline owning Director showing the "machine is offline (503)" message.
- [ ] A large PDF or image that visibly seeks/resumes in the viewer (the range path end to end).
- [ ] The macOS pass recorded on issue #125.

No real-device screenshots have been fabricated for this draft; the tables above are the slot-build
proof from Phases 2 and 3, clearly labelled as such.
