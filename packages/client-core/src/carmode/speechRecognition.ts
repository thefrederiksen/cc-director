// The lightweight, continuous control-word recognizer for Car Mode (new build B, mission Phase 1).
// This wraps the browser's built-in speech recognition (SpeechRecognition / webkitSpeechRecognition,
// solid in the Chromium web view Car Mode runs in - decision 7, Chromium only). It watches ONLY for
// the two control triggers - "over and out" to end a turn, "stop"/"wait"/"shut up" to interrupt - and
// hands each live transcript up to the caller, which decides what to do (controlPhrases.ts).
//
// It deliberately does NOT transcribe the command itself: the full utterance is captured separately by
// MicRecorder and transcribed by the Gateway (POST /wingman/transcribe) for accuracy. This recognizer's
// only job is fast, continuous control-word spotting, and it runs alongside MicRecorder on the same
// echo-cancelled microphone (decision 6). If the built-in recognizer cannot survive the assistant's own
// voice during playback (barge-in), the mission's fallback is an on-device keyword model on the
// echo-cancelled stream - proven first in Phase 1, not assumed (mission "New build B").

// The browser speech-recognition types are not in the DOM lib, so declare the minimal surface used.
interface SpeechRecognitionAlternativeLike {
  transcript: string;
}
interface SpeechRecognitionResultLike {
  readonly length: number;
  0: SpeechRecognitionAlternativeLike;
  isFinal: boolean;
}
interface SpeechRecognitionResultListLike {
  readonly length: number;
  [index: number]: SpeechRecognitionResultLike;
}
interface SpeechRecognitionEventLike {
  results: SpeechRecognitionResultListLike;
}
interface SpeechRecognitionErrorEventLike {
  error: string;
}
interface SpeechRecognitionLike {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  maxAlternatives: number;
  onresult: ((e: SpeechRecognitionEventLike) => void) | null;
  onerror: ((e: SpeechRecognitionErrorEventLike) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
}
type SpeechRecognitionCtor = new () => SpeechRecognitionLike;

function resolveConstructor(): SpeechRecognitionCtor | null {
  if (typeof window === "undefined") return null;
  const w = window as unknown as {
    SpeechRecognition?: SpeechRecognitionCtor;
    webkitSpeechRecognition?: SpeechRecognitionCtor;
  };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

/** Whether this browser exposes the built-in speech recognition Car Mode's control-word spotting uses.
 *  Car Mode is Chromium-only (decision 7); elsewhere the page tells the owner plainly instead of
 *  silently degrading (no fallback, decision 8). */
export function isControlRecognitionSupported(): boolean {
  return resolveConstructor() !== null;
}

/**
 * A continuous control-word listener. Call start() to begin spotting; the onTranscript callback fires
 * with the latest combined transcript of the CURRENT recognition segment on every interim/final result,
 * and the caller runs controlPhrases detection on it. It auto-restarts when the browser ends a segment
 * (the built-in recognizer stops itself periodically), so listening is continuous until stop().
 *
 * Enterprise behavior: every lifecycle transition is logged with a "[CarMode.recognizer]" prefix so a
 * barge-in problem is diagnosable over chrome://inspect (mission decision 9), and a fatal recognizer
 * error is surfaced to onError rather than swallowed (no silent stall).
 */
export class ControlWordListener {
  private recognition: SpeechRecognitionLike | null = null;
  private active = false;
  private onTranscript: (text: string) => void = () => {};
  private onError: (message: string) => void = () => {};

  /** True while the listener is meant to be running (survives the browser's internal segment restarts). */
  get isActive(): boolean {
    return this.active;
  }

  /**
   * Start continuous control-word spotting. Throws if the browser has no speech recognition - the
   * caller surfaces the reason (no silent fallback). Safe to call once; call stop() before start()
   * to restart cleanly.
   */
  start(onTranscript: (text: string) => void, onError: (message: string) => void): void {
    const Ctor = resolveConstructor();
    if (Ctor === null) {
      throw new Error("This browser does not support speech recognition. Car Mode needs Chrome/Chromium.");
    }
    if (this.active) return;
    this.onTranscript = onTranscript;
    this.onError = onError;
    this.active = true;
    this.spawn(Ctor);
    console.log("[CarMode.recognizer] started");
  }

  /**
   * Clear the current recognition segment and start a fresh one, so a control phrase that just fired
   * ("over and out", "stop") cannot linger in the transcript and re-trigger on the next phase. A no-op
   * when the listener is not active. The onend restart loop brings the new segment up.
   */
  reset(): void {
    if (!this.active) return;
    const r = this.recognition;
    if (r === null) return;
    try {
      // abort() ends the segment WITHOUT emitting a final result; onend then spawns a clean segment.
      r.abort();
    } catch {
      // already ending; onend will still restart while active
    }
    console.log("[CarMode.recognizer] reset");
  }

  /** Stop spotting and release the recognizer. Idempotent. */
  stop(): void {
    this.active = false;
    const r = this.recognition;
    this.recognition = null;
    if (r !== null) {
      r.onresult = null;
      r.onerror = null;
      r.onend = null;
      try {
        r.abort();
      } catch {
        // already stopping; nothing to release beyond clearing handlers above
      }
    }
    console.log("[CarMode.recognizer] stopped");
  }

  private spawn(Ctor: SpeechRecognitionCtor): void {
    const r = new Ctor();
    r.lang = "en-US";
    r.continuous = true;
    r.interimResults = true;
    r.maxAlternatives = 1;

    r.onresult = (e) => {
      // Join every result segment of the CURRENT recognition session into one string. The control
      // phrases run on this; the accurate command text comes from the Gateway transcription, not here.
      let combined = "";
      for (let i = 0; i < e.results.length; i++) {
        const result = e.results[i];
        if (result.length > 0) combined += result[0].transcript + " ";
      }
      this.onTranscript(combined.trim());
    };

    r.onerror = (e) => {
      // "no-speech" and "aborted" are normal in a walkie-talkie (the owner pauses to think for as long
      // as he likes - decision, no silence timer), so they are NOT surfaced as failures; onend restarts
      // the segment. A "not-allowed" (microphone permission) is fatal and must be spoken.
      if (e.error === "no-speech" || e.error === "aborted") {
        console.log(`[CarMode.recognizer] transient: ${e.error}`);
        return;
      }
      if (e.error === "not-allowed" || e.error === "service-not-allowed") {
        console.log(`[CarMode.recognizer] FATAL: ${e.error}`);
        this.active = false;
        this.onError("Microphone access is blocked. Allow the microphone for Car Mode and try again.");
        return;
      }
      console.log(`[CarMode.recognizer] error (will retry): ${e.error}`);
    };

    r.onend = () => {
      // The browser ends segments periodically; while we are still meant to be listening, immediately
      // spin up a fresh one so control-word spotting is continuous with no silence gap.
      if (!this.active) return;
      const Again = resolveConstructor();
      if (Again === null) return;
      this.spawn(Again);
    };

    this.recognition = r;
    try {
      r.start();
    } catch (err) {
      // start() throws if called too soon after the previous segment; the onend restart loop recovers.
      console.log(`[CarMode.recognizer] start deferred: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
