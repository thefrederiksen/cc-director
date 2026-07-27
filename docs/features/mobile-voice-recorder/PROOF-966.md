# Voice Recorder follow-up - live proof (issue devthrottle_internal#966)

Date: 2026-07-27. Rig: a LOCAL test Gateway published from this branch's exact commit and run
isolated on port 7899 (`CC_DIRECTOR_ROOT` pointed at a scratch root, `CC_GATEWAY_NO_TAILSCALE=1`
so it cannot touch the machine's 443 front door, `CC_GATEWAY_NO_AUTH=1` - the standard dev
toggles). `/healthz` reported `1.8.0+2838d9c7...`, this branch's commit, before the walk.
Production was never touched.

The walk was scripted with Playwright as a library (the same deliberate choice as the original
PR #2219 proof: a repeatable verification needing special browser launch flags - a fake
microphone fed from a synthesized speech WAV - which must not pollute the interactive
browser-harness personas). Phone viewport 390x844.

## Which links of the notes chain were broken

The owner's note died at DISPLAY, and only there:

- Capture was sound: the note landed in the durable local manifest with its millisecond offset.
- Delivery was sound: the notes rode the complete call; the Gateway persisted them in
  manifest.json and even wrote a Notes section into transcript.md.
- Display was broken: `GET /ingest/recording/{id}/transcript` - the one endpoint both the phone
  transcript view and the Cockpit Voice Recorder page render - served the bare cleaned
  transcript stamped at transcription time, which has no notes. The transcript.md that does
  carry the notes was only a fallback that never wins once a cleaned transcript exists. Neither
  surface rendered notes any other way, so a note appeared NOWHERE.

The fix folds the notes into the served transcript ON THE GATEWAY, ordered and positioned by
their offsets (the client-is-dumb rule): one edit fixes both surfaces at once, retroactively
including recordings uploaded before the fix, and the clients change nothing at all for it.

## What each screenshot shows (shots-966/)

| Screenshot | Proves |
|---|---|
| 01-recording-two-notes.png | Recording live with two timestamped notes added (at ~3 s and ~10 s). |
| 02-auto-uploading.png | Immediately after Stop with NOTHING touched: the upload has already started by itself. The script also asserted no Send button exists anywhere. |
| 03-transcribed-unaided.png | Both checks on - Uploaded and Transcribed - reached with zero interaction after Stop. |
| 04-phone-transcript-notes.png | The phone transcript view: `Notes:` block with `[00:03] First checkpoint note` and `[00:10] Second checkpoint note` above the transcript. |
| 05-cockpit-transcript-notes.png | The same recording on the Cockpit's Voice Recorder page, same notes at the same offsets. |
| 06-parked-after-dead-connection.png | The kill-connection re-run: the auto-started upload hit a dead connection at segment 1 and parked saved-and-retryable with a plain reason and a manual Retry - the only place a send button remains. |
| 07-retry-delivered.png | Retry delivered the rest; both rows Uploaded + Transcribed. |

## Kill-connection retry, re-proven with auto-send

The second walk recorded 2 segments (a Pause finalizes the open segment), armed an in-flight
abort of the FIRST PUT of segment 1, and pressed Stop. The auto-started upload sent segment 0,
died at segment 1, and parked the row saved-and-retryable. Pressing Retry on the parked row
delivered it. The script counted every segment PUT that reached the server:

- Walk B's segment 0: sent exactly once across both passes (the retry resumed at the first
  unsent segment and never re-sent bytes the server already held).
- Walk B's segment 1: aborted in-flight once, then sent once on retry.

So the per-segment resume behavior of PR #2219 holds unchanged under automatic sending.

## Suites

- Full .NET solution, Release: all seven test projects, 8,490 passed, 0 failed.
- Web: typecheck all workspaces; client-core 785 tests; cockpit 218 tests; mobile + cockpit
  production builds - all green.
- The new Gateway test (`GetTranscript_FoldsNotesIntoServedText`) was run against the unfixed
  code first and failed exactly as intended, then passed with the fix.

## Cleanup

The test Gateway was shut down after the walk (`POST /shutdown`), the process verified dead by
path, and the `rig966-gateway` scheduled task deleted. The isolated scratch root kept everything
off the real install.
