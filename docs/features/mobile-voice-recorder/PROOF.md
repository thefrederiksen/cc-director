# Mobile PWA Voice Recorder - live proof (issue devthrottle_internal#958)

Date: 2026-07-27. Rig: a LOCAL test Gateway published from this branch's exact commit and run
isolated on port 7899 (`CC_DIRECTOR_ROOT` pointed at a scratch root, `CC_GATEWAY_NO_TAILSCALE=1` so
it cannot touch the machine's 443 front door, `CC_GATEWAY_NO_AUTH=1` - the standard dev toggles).
`/healthz` confirmed the running build's commit before the walk. Production was never touched.

The walk was scripted with Playwright as a library (deliberate per the tooling rules: a repeatable
verification needing special browser launch flags - a fake microphone fed from a synthesized speech
WAV - which must not pollute the interactive browser-harness personas). Phone viewport 390x844.

## What each screenshot shows

| Screenshot | Proves |
|---|---|
| 01-menu-voice-recorder.png | The nav drawer has a Voice Recorder entry. |
| 02-recorder-idle.png | The recorder screen: timer card, title entry, big red Record. |
| 03-recording-with-note.png | Recording live: timer running, level meter moving, a timestamped note added at 10 s. |
| 04-paused.png | Pause finalizes the open segment and suspends capture. |
| 05-stopped-ready.png | Stop produces a library row: saved on this phone, ready to send. |
| 06-survives-reload.png | The page was RELOADED before upload; the recording is still there (durable IndexedDB copy written during capture, before any network work). |
| 07-send-interrupted-saved.png | Send with the connection killed mid-upload (the PUT of segment 1 was aborted): the row parks as saved-and-retryable with a plain reason. Nothing lost. |
| 08-transcribing.png | Retry delivered the rest; the server ACKed the complete call and queued transcription. |
| 09-transcribed.png | Both independent status checks: Uploaded and Transcribed. |
| 10-transcript-on-phone.png | The transcript, readable on the phone row. |
| 11-recovered-after-close.png | A second capture was abandoned mid-recording by a hard navigation (the app "closing"); on return the recording was recovered with its finalized segments intact and offered back. |
| 12-unknown-route.png | An unknown route now says "Page not found - this page does not exist" and links home (it no longer tells the user to refresh). |
| 13-cockpit-transcripts.png | The same recording on the Cockpit's Voice Recorder page (GET /ingest/recordings). |
| 14-cockpit-transcript-open.png | The transcript open on the Cockpit. |

## Per-segment resume, measured

The proof script counted every chunk PUT that reached the server across the interrupted send and
the retry:

- Successful PUTs per segment index across BOTH passes: segment 0 = 1, segment 1 = 1, segment 2 = 1
  (the first attempt at segment 1 was aborted in-flight and never reached the server).

Segment 0 was sent exactly once: the retry resumed at the first unsent segment and never re-sent
bytes the server already held.

## Transcript

The fake microphone played a synthesized speech WAV. The Gateway's transcript of it:

> [00:00] This is a test recording from the Dev Throttle mobile voice recorder. The quick brown fox
> jumps over the lazy dog. Segment rotation keeps every minute of audio safe on the phone before
> upload. (The fake microphone loops the WAV, so the text repeats across the three segments.)

## What the web platform cannot match from the native app

The retired Android recorder drained its upload queue from a WorkManager job under a wakelock, so
an upload continued even after the app was swiped away. A browser cannot do that: once the tab is
killed, no page code runs. What the PWA does instead - and what the durability walk above proves -
is the same guarantee moved one step later: every segment and the manifest are durable on the phone
before and during upload, and delivery resumes automatically the next time the app opens (and when
connectivity returns while it is open). One-shot Background Sync exists in Chromium and could
shorten that window by retrying once from the service worker; it would mean duplicating the upload
driver inside the service worker and is left as a candidate follow-up, not silently half-done.

## Cleanup

The test Gateway was shut down after the walk and its scheduled task deleted; the process was
verified dead (no devthrottle-gateway.exe running from the scratch stage). The isolated scratch
root kept everything off the real install.
