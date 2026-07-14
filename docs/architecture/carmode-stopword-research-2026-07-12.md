# Car Mode stop-word detection - research and settled design (issue #1411)

Status: research settled by the Car Mode Architect, 2026-07-12. This is the agreed approach the
Manager implements. Child of the Car Mode epic (#1410); worked before the system-prompt issue (#1412).

## The problem, precisely

Car Mode is a walkie-talkie. The owner cuts the assistant off mid-reply by saying a **stop word**.
Detecting that spoken word **while the assistant's own voice is playing out of the phone speaker**
(barge-in), fast enough to feel instant, is the one unsolved unknown in the mission.

Current shipped state (verified against `origin/main` @ `0910530a`, v5 - NOT the working tree):
- The browser's built-in speech recognizer was removed (pull request #1358): flaky on Android and
  untestable with fake audio.
- The end phrase "over and out" is caught by pause-triggered Gateway transcription; that path works.
- There is **no spoken interrupt during playback at all** today. The only interrupt while the
  assistant speaks is the on-screen **STOP button**. Spoken "stop" during playback was removed in v3
  because listening during playback re-opened the microphone and ducked the reply audio to silence.
- `micReacquiredDuringPlayback` in the telemetry is a **hardcoded `false` assertion** ("kept to
  prove it"), not a live measurement. If any new approach re-adds a microphone tap during playback,
  that flag must be turned back into a real measurement and the mobile duck re-checked before shipping.

So this issue is about bringing the spoken stop word back, reliably, without re-triggering the duck.

## Why the cloud-transcription approach cannot be the answer

Sending rolling audio windows to the Gateway to be transcribed and scanned for "stop" has two fatal
problems for an interrupt: a cloud round-trip is far too slow for something that must feel instant
(hundreds of milliseconds to seconds), and to hear the owner over the reply it must keep the
microphone open during playback - the exact thing that ducks the audio. An interrupt has to be
detected **on the device, on the stream we already control, with no round-trip.**

## The candidates evaluated

| Option | In-browser | Latency | Custom words | License / cost | Verdict |
|---|---|---|---|---|---|
| **TensorFlow.js Speech Commands** | Yes (WebGL + WebAudio FFT) | Near-instant, warm-up-able | Fixed 18-word vocab out of the box (includes **"stop"**); custom via in-browser transfer learning (record ~8 samples) | Apache-2.0, free, no key | **CHOSEN** |
| Picovoice Porcupine | Yes (WASM, fully offline) | Excellent | Arbitrary, typed-in phrase | Free tier = single device + watermarked; custom words need Enterprise; commercial from **$6,000/year** | **Rejected** - conflicts with MIT / free / pay-only-for-services |
| openWakeWord | Possible (onnxruntime-web) | Good | Needs ML training | Open source | Fallback only if TF.js accuracy is insufficient |
| Gateway rolling transcription (today) | No (round-trip) | Too slow; ducks audio | Any word | n/a | Rejected for interrupt |

## The decision

**Use TensorFlow.js Speech Commands (`@tensorflow-models/speech-commands`).** It runs entirely in the
phone browser on the microphone stream we already echo-cancel, needs no server round-trip, no
access key, and its built-in model already recognizes **"stop"** - the natural default. It warms up
the same way our keep-warm pattern already does for the model and text-to-speech.

Porcupine is technically the strongest but its licensing (single-device free tier, Enterprise-gated
custom words, $6,000/year floor) is incompatible with DevThrottle being open source and free. We do
not take a $6,000/year dependency for one control word.

### User-configurable stop word (the Settings requirement)

Two tiers, shipped in order:

1. **Pick from the known vocabulary (ship first).** The built-in model recognizes a fixed set
   ("stop", "go", "up", "down", "yes", "no", "left", "right", "on", "off", the digits). Let the user
   choose their stop word from the subset that makes sense as a "cut it off" word. Zero training,
   instant, reliable. Default = "stop".
2. **Custom word via transfer learning (later).** For a word not in the vocabulary, TF.js supports
   in-browser transfer learning: the user records a handful of samples and a tiny model is trained on
   the device. The Settings Test button flow (below) doubles as the sample-collection UI. This is a
   follow-up enhancement, not required for the first ship.

## The offline test harness (build this first - the iteration loop)

The owner's hard requirement: evaluate stop-word detection **outside the app, with no phone deploy**.

Design - drive the **real** on-device path headlessly so results transfer to the phone:

- A tiny HTML page loads `@tensorflow-models/speech-commands`, warms up, calls `listen()`, and logs
  every detection with a high-resolution timestamp and score.
- Playwright launches Chromium with `--use-file-for-fake-audio-capture=<clip>.wav`,
  `--use-fake-device-for-media-stream`, and an autoplay-permissive policy, so **WebAudio's real FFT**
  processes each clip exactly as it would a live microphone on the phone. This is the same fake-audio
  technique the mission already proved for `MicRecorder`; it is why the numbers transfer.
- For each labelled clip the runner records: detected (yes/no), the score, and the latency from clip
  start to detection.
- Scorecard aggregates: **detection rate** on true positives, **false-fire rate** on negatives, and
  **median / p95 latency**.

### Test corpus (labelled clips)

- True positives: "stop", "stop stop", "please stop", spoken at different speeds/volumes.
- Near-misses that must NOT fire: "stopwatch", "stopping", "nonstop", "the bus stop", "top", "shop".
- The barge-in case: the assistant's own reply audio playing, with a spoken "stop" mixed over it at
  realistic speaker/mic levels (the hardest and most important case).
- Environment: quiet, background noise, simulated in-car noise.
- Seed the corpus synthetically via the Gateway voice (`POST /wingman/tts`) plus a few real phone
  recordings captured over `chrome://inspect`; mix the assistant-under-owner clips programmatically.

The harness lives outside the product (a research tool, not shipped code) and is the loop we iterate
the detector and thresholds on before anything touches the phone.

## What "done" looks like for the detector choice

Measured evidence from the harness that TF.js Speech Commands, on the echo-cancelled stream:
- detects "stop" reliably, including **while the assistant's reply is playing**, at low latency; and
- does not false-fire on the near-miss and assistant-reply-only clips.

If (and only if) the harness shows TF.js cannot clear the barge-in case, escalate to openWakeWord on
the same harness before considering any paid engine. Prove first; do not assume.

## First measured findings (2026-07-12, harness built, synthetic corpus)

The offline harness is built and running (Playwright + Chromium `--use-file-for-fake-audio-capture`
driving the real TF.js Speech Commands + WebAudio FFT path; corpus synthesized from the Gateway
voice). First three passes, synthetic (text-to-speech) audio only - real human recordings still to come:

- **Specificity is excellent.** Seven distinct near-misses that do not contain the word "stop"
  (shop, top, hop, drop, pop, "start the tests", "the server crashed") every one scored below 0.10.
  The model does not confuse similar-sounding words. False-fire rate on these = 0.
- **Sensitivity to a clearly-spoken "stop" is good but not yet perfect on synthetic audio.** Repeated
  "stop" clips peaked at 0.99+; a couple were weak (a "stop now" clip peaked 0.28). At a single-frame
  threshold overall detection was ~0.83. Single short utterances swing run-to-run because a ~1 second
  model window may miss one brief word - repeating the word removes that lottery; real human
  recordings and threshold tuning are needed for a trustworthy sensitivity number.
- **THE LOAD-BEARING FINDING: the assistant's own voice can self-trigger the detector.** A
  full-volume assistant reply containing no "stop" scored 0.99 and false-fired. This is the barge-in
  risk made concrete: if the stop detector runs during playback and hears the assistant's own speech,
  it may self-interrupt. In this test the assistant was at full volume (the worst case, no echo
  cancellation). The realistic case is the residual the microphone hears AFTER echo cancellation
  (heavily attenuated) - so the decisive next test is a negative clip of attenuated assistant audio
  only (no owner "stop"), across several different replies, to see whether the residual stays below
  threshold. If attenuated assistant speech still self-triggers, the detector cannot run naively
  during playback and needs a gate (for example: suppress detection on the frequencies/known audio the
  assistant is currently producing, or accept "stop" only when its score clears the assistant-voice
  floor), or barge-in stays button-only while Speaking with the spoken stop working only in Listening.

**The decisive test is now run, and the answer is that attenuation does NOT save us.** Two real
assistant replies (the actual nova Gateway voice), each tested at full volume and attenuated to 0.35
(the residual after echo cancellation), with no "stop" spoken:
- Reply 1 scored **1.0 even at 0.35 attenuation**, sustained across frames - so neither a higher
  threshold nor a longer debounce suppresses it.
- Reply 2 was borderline (0.98 full, 0.69 attenuated).

Conclusion: **the generic TF.js Speech Commands "stop" class cannot safely run a spoken interrupt
during playback.** It is specific against distinct standalone words (all seven near-misses silent),
but the coarse "stop" classifier is triggered at 1.0 by segments of ordinary connected speech,
including the assistant's own voice, independent of volume. A spoken interrupt built on it would
self-cut certain replies. Echo-cancellation loudness reduction is not a reliable fix because the
model is largely volume-robust.

### Revised recommendation

- **Spoken barge-in (interrupt while the assistant is speaking) needs a purpose-trained wake-word
  model, not the generic 18-word classifier.** A model trained to fire ONLY on the chosen word, with
  general speech (and ideally the assistant's own voice) as negatives, is far more specific. The
  open-source, self-hostable option is **openWakeWord** (custom words via training); Picovoice
  Porcupine's purpose-trained words are excellent but its licensing is still out. This is a larger
  build (train + host a small model) and is the real path if spoken barge-in during playback is
  wanted.
- **The generic TF.js model is still useful where the assistant is NOT speaking** - but in the
  Listening phase the turn is already ended by "over and out", so a stop word there adds little. The
  value of the stop word is barge-in, which is exactly the case the generic model fails.
- **Interim shipping option:** keep today's v5 behaviour - the STOP button is the interrupt during
  playback (reliable, zero risk) - and treat spoken barge-in as its own tracked piece pending the
  purpose-trained model. The configurable-stop-word Settings tab and Test button still make sense,
  built on whichever detector we land on.

Still to do regardless of path: real human "stop" recordings captured over `chrome://inspect` (the
owner-side audio here is text-to-speech, a weak proxy), and re-running the harness against the chosen
detector. The harness itself is the durable asset - it now evaluates any detector against the same
corpus with no phone deploy.

## Implementation order (after the harness proves the detector)

1. Offline test harness + corpus, detector chosen and thresholds tuned on measured numbers.
2. A `useStopWord` detector in `packages/client-core/src/carmode/` wrapping the chosen engine on the
   single `MicRecorder` stream, live during Speaking. If it taps the mic during playback, convert
   `micReacquiredDuringPlayback` back to a live measurement and re-check the mobile duck on a real
   phone before shipping.
3. A Cockpit Settings tab to choose the stop word, with a Test button that runs the same detector so
   the user can confirm capture (and, later, collect transfer-learning samples).
4. Wire the chosen stop word into Car Mode's interrupt path; owner-verify on the real phone.
