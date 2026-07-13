# Car Mode offline-resilience proof

The automated proof for Car Mode Phase 4a (bad-connection / offline resilience, issue #1427). It mounts
the REAL shipping `useCarMode()` turn machine in a real Chromium and drives one turn across a simulated
connection drop, proving the three mission guarantees with hard evidence.

## Run

```
cd packages/client-core/browser-tests/carmode-offline-proof
node run-proof.mjs
```

It esbuild-bundles `harness-entry.tsx` (which imports the real `useCarMode`, `pendingTurnStore`,
`turnRetry`, and `carModeApi`), serves this directory over http, launches Chromium with a fake audio
capture device fed a tone-then-silence WAV, and drives the scenario with Playwright. It writes
`evidence-<date>.json` plus `evidence-1-holding.png` and `evidence-2-recovered.png`, and exits non-zero
on any failed claim.

Playwright is resolved from the global `@playwright/cli` install; override with `PLAYWRIGHT_PATH` if it
lives elsewhere.

## The scenario and the three claims

1. Start Car Mode, capture a real (fake-device) utterance.
2. Go offline, then end the turn -> the transcribe fails mid-turn.
   - **Claim 1 - no speech lost:** the command AUDIO is still in the durable IndexedDB store
     (`dt-carmode` / `pending-turns`), a real non-empty Blob, `brainSent` false. The failed transcribe did
     not discard it.
3. The end-phrase watch keeps failing while offline.
   - **Claim 3a - connection-down announced:** after several failed ticks the connection-down line is
     spoken (captured `speechSynthesis`).
4. Reconnect and fire the browser `online` event -> the background driver re-drives the held turn.
   - **Claim 2 - auto-complete on reconnect:** the reply appears and the durable record is DELETED (turn
     owned server-side, so it can never double-act).
   - **Claim 3b - holding + recovery announced:** the holding line was spoken locally, and the recovered
     reply went to the good voice WITH the "Back online" prefix.

## What it covers, and what it does NOT

REAL: the shipping `useCarMode` turn machine, `MicRecorder` capture (fake audio device), the WebM->WAV
transcode, the durable IndexedDB store, the classify/cadence policy, and the re-drive driver - all in a
real Chromium.

NOT the real phone: no real microphone, no mobile audio-session behaviour (ducking / autoplay), no real
radio offline, and the Gateway (transcribe / brain / text-to-speech) is a controllable in-page shim, not
the live server. The real-phone offline-mid-turn pass and the owner's by-hand pass remain the on-device
confirmation.
