# Issue 1210 - Composer completion - proof

Phase 1 of the Cockpit improvement plan. The Cockpit composer bottom row is now
Send, Speak, Queue, Attach, and Attach / clipboard paste / drag-and-drop all route
through the single shared `uploadImage` call.

## What changed

- `apps/cockpit/src/sessions/SessionComposer.tsx`
  - Added a Speak button (between Send and Queue) that mounts the shared
    `DictationDialog` from `packages/client-core`, wired exactly like the mobile
    `SessionControls` (onInsert / onSend / onSendAudio / onClose). No new
    dictation, recording, or transcription code - one transcription path through
    the Gateway, unchanged.
  - Refactored the image upload into one `attachFiles(files)` helper. Attach,
    clipboard paste (textarea `onPaste`), and drag-and-drop (`onDrop`) all call it,
    so there is exactly one upload code path (the shared `uploadImage`).
- `apps/cockpit/src/styles.css` - a `.composer-dragover` affordance while a file is
  dragged over the composer.

## Local verification (dev server against the live Gateway on this machine)

The Cockpit dev server (`npm run dev`, `COCKPIT_PROXY_TARGET=http://127.0.0.1:7878`)
was pointed at the live Gateway and driven with Playwright.

1. `composer-four-buttons.png` - the composer shows four buttons in order:
   Send, Speak, Queue, Attach.
2. `composer-speak-dialog.png` - clicking Speak opens the shared dictation dialog
   (Cancel / Pause / Insert / Send), identical to the mobile Speak flow.

## Automated checks

- `tsc --noEmit` (cockpit) - clean.
- `vite build` (cockpit) - clean.
- `vitest run` (client-core) - 148 tests pass (no shared-package regression).
- Mobile app unchanged (no shared-package behavioral change; no mobile files touched).

## Single-upload-path (code inspection)

`onAttach`, `onPaste`, and `onDrop` in `SessionComposer.tsx` all call the single
`attachFiles` helper, which is the only caller of `uploadImage`. There is no second
upload code path.

## Owner-hardware acceptance items (need a second device / real microphone)

These acceptance criteria require a remote browser (phone or second machine) and/or a
real microphone + transcription round trip, which cannot be produced from this
headless harness. Please verify:

- Dictate a sentence from a phone browser and confirm the transcript lands in the
  composer and the Gateway transcription request originates from the Cockpit page.
- From a browser on a different device than the session's machine, Attach an image
  and confirm the agent on the session's machine reads the inserted Director-side
  path.
- Paste and drag-and-drop an image from a remote machine; confirm the inserted path
  is the Director-side path.
- Force an upload/dictation failure (drop the network) and confirm the composer
  shows the existing error span (never a silent failure).
