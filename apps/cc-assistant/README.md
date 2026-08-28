# CC Assistant

A voice assistant you talk to, where the wake word is whatever each person decides it should be.

Built one step at a time, and each step answers a question that would invalidate the next one.
Right now there is exactly one step in here.

## Step 1, which is what exists: can this device hear?

Everything else sits on the answer to one question. The wake word is a text match on a transcript.
The command is a transcript. So the first thing to know is whether a phone can produce that
transcript **continuously**, and how good it is.

The measurement is a single number: the **real-time factor**, meaning the time the model takes to
transcribe a chunk divided by the real time that chunk covers.

- Under 1 and it keeps up.
- Over 1 and it does not degrade, it falls behind and never catches up. The backlog counter on
  screen is there to make that visible, because an average can look fine while a queue quietly grows.

## Running it on a computer

```
npm install
npm run dev
```

Open <http://localhost:5183/cc-assistant/>.

Load a model first. The first load downloads it, which is roughly 40 MB for tiny, 80 MB for base and
250 MB for small, and the browser caches it afterwards. Then press Start listening and talk.

## Running it on a phone

The dev server listens on the network, so the phone can reach the machine. **But the microphone will
not work over plain HTTP.** Browsers only allow microphone access on a secure connection, and
`localhost` is the only exception. Going to `http://192.168.1.x:5183` from a phone will load the page
and then refuse to open the microphone, which looks like a bug in this app and is not.

Serve it over Tailscale, which already has certificates for the tailnet:

```
tailscale serve --bg --https 8443 http://127.0.0.1:5183
```

Then open `https://soren-north.taildb08ed.ts.net:8443/cc-assistant/` on the phone. To install it to
the home screen, use the browser's Add to Home Screen. On iPhone that also matters for a later step:
holding the screen awake only works from an installed web app, and only on iOS 18.4 or newer.

## Reading the screen

| What | What it means |
| --- | --- |
| Real-time factor | The verdict. Under 0.5 is comfortable, under 1 is usable, over 1 is unusable at this size. |
| Per chunk | Milliseconds of actual model time, averaged over the last ten. |
| Backlog | Chunks waiting to be transcribed. Anything that keeps climbing means it is losing. |
| Peak | Loudest sample in the chunk, 0 to 1. Tells silence apart from a broken microphone. |
| Text | What it heard. This is the quality half of the question, and only your ears can judge it. |

Both halves matter and they pull against each other. A model that keeps up but mishears you is no
more useful than one that is accurate and too slow.

## What to try

1. **whisper-base.en on WebGPU, 2 second chunks.** The expected answer, and where to start.
2. **The same on WebAssembly.** Shows what happens on a device without WebGPU, which is roughly
   30 percent of phones. Expect it to be several times slower.
3. **whisper-tiny.en.** If base cannot keep up, this is where the listening half goes.
4. **whisper-small.en.** Almost certainly too slow to run continuously. Worth confirming, because if
   it is fast enough for one utterance it is a candidate for transcribing the command after the wake
   word fires, which is a different job with different requirements.
5. **Play music through the same machine while you talk.** The microphone is opened with echo
   cancellation and the log says whether the browser actually granted it.

## What is here

| File | What it does |
| --- | --- |
| `public/pcm-worklet.js` | Runs on the audio thread, batches raw samples, posts them out. |
| `src/audio/microphoneCapture.ts` | Opens the microphone asking for echo cancellation, then reads back whether it was granted. |
| `src/audio/pcmCapture.ts` | Converts from the device's sample rate to the 16,000 the model needs, and hands out fixed-length chunks. |
| `src/transcribe/transcriber.worker.ts` | The speech model on its own thread, and the timing. |
| `src/transcribe/transcriberClient.ts` | The page's side of that worker, and the backlog count. |
| `src/wakeWord/wakeWordMatcher.ts` | Finds a chosen word in a transcript. Written and tested, not yet wired to anything. Step 2. |
| `src/screenWakeLock.ts` | Holds the screen awake. Not yet wired. A later step. |

Run the tests with `npm test`.

## The steps after this one

Each is deliberately separate so a bad result in one does not sink the rest.

2. **Wake word.** Match a chosen word in the transcript, with learned aliases for what the recognizer
   actually hears when it gets it wrong.
3. **Voice activity detection.** Only run the model when somebody is speaking, so a quiet kitchen
   costs nothing.
4. **Turn taking.** Endpointing that adapts to the sentence, the acknowledgement sound, and letting
   the sound be retracted when the endpoint was called wrong.
5. **The brain.** Connect to the existing `/assistant/turn`.
6. **The fast path.** Timers and stop and cancel answered on the phone without touching the network.

## Known, and deliberately not solved yet

- **The model downloads from Hugging Face's servers.** Fine for a test. Before this is something you
  rely on in a kitchen the weights should be served by the Gateway and cached, or the app is broken
  the first time Hugging Face has a bad day.
- **Nothing is connected to an assistant.** This step only proves the device can hear.
