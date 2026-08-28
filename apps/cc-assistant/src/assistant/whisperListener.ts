// Listening with Whisper, which we have measured, instead of the platform recogniser, which we have
// not been able to make produce a single word.
//
// On 28 August the browser's own SpeechRecognition started and immediately ended, with no error and
// no results, in two separate Chrome profiles on a machine whose microphone was demonstrably working
// and whose permission was granted. It looked exactly like a dead microphone. Whisper on the same
// machine and on an Android phone had already been measured transcribing correctly, so the proven
// thing does the listening and the unproven thing has to earn its place back.
//
// This exposes the same shape as the platform listener so the screen does not care which is running.

import { startPcmCapture, type PcmCapture } from "../audio/pcmCapture";
import { createTranscriber, type DecoderPrecision, type Transcriber, type TranscriberDevice } from "../transcribe/transcriberClient";
import type { ListenerEvents } from "./speech";

export interface WhisperListenerOptions {
  readonly modelId: string;
  readonly device: TranscriberDevice;
  readonly decoderPrecision: DecoderPrecision;
  readonly chunkSeconds: number;
  readonly workletUrl: string;
}

export interface WhisperListener {
  stop(): Promise<void>;
  readonly ready: boolean;
}

// Speech that lands across a chunk boundary would otherwise be split into two halves, neither of
// which contains the whole wake word. Every emitted line carries the previous chunk in front of it so
// a word cut in half by the clock is still there to be matched.
const WINDOW = 2;

/** Silence transcribes as a handful of stock phrases. Emitting them would fill the screen with noise. */
const EMPTY_SOUNDS = new Set([
  "", "you", "thank you", "thanks for watching", "thank you.", ".", "bye", "so", "uh", "um",
]);

export async function startWhisperListener(
  options: WhisperListenerOptions,
  events: ListenerEvents,
): Promise<WhisperListener> {
  let capture: PcmCapture | null = null;
  let transcriber: Transcriber | undefined;
  let ready = false;
  const recent: string[] = [];

  await new Promise<void>((resolve, reject) => {
    transcriber = createTranscriber({
      onLoading(percent, file) {
        events.onNotice(percent === null ? `Loading ${file}` : `Loading the speech model, ${percent}%`);
      },
      onLoaded() {
        ready = true;
        events.onSession(true);
        events.onNotice("Speech model ready.");
        resolve();
      },
      onResult(text) {
        const trimmed = text.trim();
        if (EMPTY_SOUNDS.has(trimmed.toLowerCase())) {
          return;
        }
        recent.push(trimmed);
        while (recent.length > WINDOW) {
          recent.shift();
        }
        // Every chunk is a settled transcript: Whisper does not revise, so there are no interim
        // results to offer and everything it says is final.
        events.onHeard({ text: recent.join(" "), isFinal: true });
      },
      onFailure(message) {
        if (!ready) {
          reject(new Error(message));
          return;
        }
        events.onNotice(message);
      },
    });
    transcriber!.load(options.modelId, options.device, options.decoderPrecision);
  });

  try {
    capture = await startPcmCapture(options.chunkSeconds, options.workletUrl, (chunk) => {
      transcriber?.submit(chunk.samples, chunk.seconds);
    });
  } catch (error) {
    transcriber?.dispose();
    throw error;
  }

  return {
    get ready() {
      return ready;
    },
    async stop() {
      events.onSession(false);
      const toStop = capture;
      capture = null;
      transcriber?.dispose();
      transcriber = undefined;
      if (toStop !== null) {
        await toStop.stop();
      }
    },
  };
}
