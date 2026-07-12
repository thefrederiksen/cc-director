# Car Mode - "the whole reply is heard" audio-event proof

This is the real-browser test for the Car Mode cut-off-reply fix. It answers the recurring question -
"how do we test this without listening?" - by instrumenting the real `<audio>` element and asserting the
whole reply plays from `play` to `ended` with no `src` change before `ended`.

## What it proves

It drives the ACTUAL product playback leaf - `playClip` from
`packages/client-core/src/carmode/audioPlayback.ts` - on a REAL `<audio>` element with a REAL audio clip,
and records every `src` assignment plus the `play` / `playing` / `ended` events with timestamps.

- **Case A (product single-clip playback):** synthesize the whole reply as one clip and play it through
  `playClip`. It passes only when the outcome is a natural `ended`, the element fired both `play` and
  `ended`, the `src` was assigned EXACTLY ONCE, nothing clobbered it mid-play, and it stayed audible for
  (near) the whole clip - i.e. the ENTIRE reply is heard, not just the tail.
- **Case B (the guard has teeth):** deliberately clobber one element - start clip 1, then overwrite its
  `src` with clip 2 while clip 1 is still playing. The instrument must flag "src changed before ended". If
  it does, Case A's pass is not vacuous: the assertion would fail on a real clobber.

The playback is started from a real button tap, because a browser's autoplay policy only permits playback
after a user gesture - the same reason Car Mode itself only starts speaking after the owner taps Start.

## How to run

```
node build-and-run.mjs
```

This bundles the product `playClip` into `audioPlayback.iife.js` (a build artifact, git-ignored) and serves
`harness.html` at http://127.0.0.1:8791/harness.html. Open that URL in a real browser and press **Run
test**. The verdict is shown on the page and is also available as `window.__RESULT__` (the machine-readable
form driven headless via browser-harness with a trusted CDP click).

The unit-level invariant (src assigned exactly once, ended/stopped/error outcomes, lifecycle hooks) is also
covered without a browser in `packages/client-core/src/carmode/audioPlayback.test.ts`.

## Captured evidence

- `evidence-2026-07-11.json` - the `window.__RESULT__` from a passing run (both cases PASS).
- `evidence-screenshot.png` - the page after a passing run.
