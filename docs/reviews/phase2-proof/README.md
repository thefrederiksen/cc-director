# Local Files - Phase 2 proof (cockpit file viewer)

These screenshots prove the Phase 2 deliverables end to end in the running React cockpit:
the `FileViewerModal`, the clickable chat path link, and the xterm terminal link provider.

## How this was captured

- A slot-5 Director (`cc-director5.exe`) was built from this branch (off `origin/main`, which
  carries Phase 1 `GET /sessions/{sid}/file`). NOTE: the live fleet Director/Gateway still
  predates Phase 1, so the proof ran against the fresh slot-5 build; deploying Phase 1+ to the
  live Gateway is a mission-level step handled before real-machine sign-off (Phase 4).
- A proof session was created on that Director, and five fixture files (one per render mode)
  were placed at `D:\p2proof\`: `proof.png`, `report.pdf`, `report.html` (self-contained
  cc-html output), `report.md`, `sample.py`.
- The cockpit dev server was run with `COCKPIT_PROXY_TARGET` pointed at that Director, so the
  browser talked to the real `/sessions/{sid}/file` endpoint the same way it would through the
  Gateway (the Gateway leg is the existing per-session catch-all proxy, already verified in
  Phase 1).
- Each file path was clicked in the Chat tab AND in the Terminal, and the resulting render was
  screenshotted.

## Chat clicks (the inert copy-only span is now a clickable link; the copy button is kept)

- `chat-1-image.png` - image (`proof.png`) rendered with `<img>`.
- `chat-2-pdf.png` - pdf (`report.pdf`) in the browser's native PDF viewer (`<iframe>`).
- `chat-3-html.png` - html (`report.html`) in the SANDBOXED iframe (`sandbox="allow-scripts"`,
  no `allow-same-origin`); the inlined cc-html theme CSS renders fully.
- `chat-4-markdown.png` - markdown (`report.md`) rendered with the shared sanitized
  `markdownToHtml()`.
- `chat-5-text.png` - text/code (`sample.py`) shown in a monospace `<pre>`.

## Terminal clicks (new xterm link provider over the rendered line text)

- `term-1-image.png` through `term-5-text.png` - the same five files, opened by clicking the
  underlined path in the live terminal. The link provider underlines only the detected path
  span and routes a file-path click to the app's `onFileLink` handler, which opens the viewer.

## Not covered here (by design)

- The `download` mode (unknown/binary) shows a filename + Download link; it and richer error
  states are Phase 4 polish.
- HTML files that reference sibling assets by relative path do not resolve them (single-file
  load, not a directory) - a stated v1 limitation; self-contained reports (cc-html) render
  correctly, which is the primary case.
