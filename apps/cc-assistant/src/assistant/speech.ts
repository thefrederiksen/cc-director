// Listening and talking, using what the browser already has.
//
// No model, no download, no key. The platform's own recogniser does the listening and its own voice
// does the speaking. It is free, it starts instantly, and on Chrome 139 and later the listening can
// be kept entirely on the device. Whisper is better at transcribing a known clip; it is not better
// at being available the moment somebody opens the page, which is what this needs to be.

export type ListenerState = "stopped" | "listening";

export interface Heard {
  readonly text: string;
  readonly isFinal: boolean;
}

export interface Listener {
  start(): void;
  stop(): void;
  readonly state: ListenerState;
  /** True when the browser agreed to keep the audio on this device. Shown, never assumed. */
  readonly local: boolean;
}

export interface ListenerEvents {
  onHeard(heard: Heard): void;
  onNotice(message: string): void;
  onFatal(message: string): void;
  /** Fires every time the browser starts or ends a recognition session, which it does constantly. */
  onSession(running: boolean): void;
}

interface RecognitionLike {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  maxAlternatives: number;
  processLocally?: boolean;
  onresult: ((event: { resultIndex: number; results: { length: number; [i: number]: { isFinal: boolean; [j: number]: { transcript: string } | undefined } } }) => void) | null;
  onerror: ((event: { error: string }) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
}

type RecognitionConstructor = new () => RecognitionLike;

function constructorOf(): RecognitionConstructor | null {
  if (typeof window === "undefined") {
    return null;
  }
  const w = window as unknown as { SpeechRecognition?: RecognitionConstructor; webkitSpeechRecognition?: RecognitionConstructor };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

/** Whether on-device recognition is ready for this language, so the caller can say which is in use. */
export async function localRecognitionReady(language: string): Promise<boolean> {
  const ctor = constructorOf() as (RecognitionConstructor & { available?: (o: unknown) => Promise<string> }) | null;
  if (ctor === null || typeof ctor.available !== "function") {
    return false;
  }
  try {
    return (await ctor.available({ langs: [language], processLocally: true })) === "available";
  } catch {
    return false;
  }
}

/**
 * Listen continuously until stopped.
 *
 * The browser ends a continuous session on its own roughly every minute and after long silences, so
 * restarting is normal operation rather than error recovery. Restarting instantly and repeatedly is
 * not, and that case stops and says so instead of spinning.
 */
export function createListener(language: string, preferLocal: boolean, events: ListenerEvents): Listener {
  const ctor = constructorOf();
  if (ctor === null) {
    throw new Error("This browser cannot listen. Chrome and Edge can; Firefox cannot.");
  }

  let recognition: RecognitionLike | null = null;
  let wanted = false;
  let local = false;
  let immediateStops = 0;
  let startedAt = 0;

  function begin(): void {
    const made = new ctor!();
    made.lang = language;
    made.continuous = true;
    made.interimResults = true;
    made.maxAlternatives = 1;
    // ONLY when the device can actually do it. Asking for local-only recognition on a device whose
    // on-device model is merely "downloadable" gets you a recogniser that reports nothing at all, and
    // the failure is silent - which is precisely what happened on 28 August and looked like a dead
    // microphone. The caller checks availability and passes the answer in; this never assumes it.
    if (preferLocal) {
      made.processLocally = true;
      local = made.processLocally === true;
    } else {
      local = false;
    }

    made.onresult = (event) => {
      for (let i = event.resultIndex; i < event.results.length; i += 1) {
        const result = event.results[i];
        const alternative = result[0];
        if (alternative !== undefined) {
          events.onHeard({ text: alternative.transcript, isFinal: result.isFinal });
        }
      }
    };

    made.onerror = (event) => {
      switch (event.error) {
        case "no-speech":
          events.onNotice("A stretch of silence. Still listening.");
          return;
        case "aborted":
          return;
        case "not-allowed":
        case "service-not-allowed":
          wanted = false;
          events.onFatal("Microphone permission was refused. Allow it for this page and start again.");
          return;
        case "audio-capture":
          wanted = false;
          events.onFatal("No microphone could be opened.");
          return;
        case "network":
          events.onNotice("The recogniser could not reach the network. Retrying.");
          return;
        default:
          events.onNotice(`Recogniser said: ${event.error}`);
      }
    };

    made.onend = () => {
      events.onSession(false);
      if (!wanted) {
        return;
      }
      immediateStops = Date.now() - startedAt < 250 ? immediateStops + 1 : 0;
      if (immediateStops >= 5) {
        wanted = false;
        events.onFatal("Listening stopped immediately five times over. It cannot run here right now.");
        return;
      }
      begin();
    };

    recognition = made;
    startedAt = Date.now();
    try {
      made.start();
      events.onSession(true);
    } catch (error) {
      wanted = false;
      events.onFatal(`Listening would not start: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  return {
    start() {
      if (wanted) {
        return;
      }
      wanted = true;
      immediateStops = 0;
      begin();
    },
    stop() {
      wanted = false;
      if (recognition !== null) {
        recognition.onend = null;
        recognition.abort();
        recognition = null;
      }
    },
    get state() {
      return wanted ? "listening" : "stopped";
    },
    get local() {
      return local;
    },
  };
}

/** Say something out loud. Resolves when it has finished, or when it is cut off. */
export function speak(text: string, onStart?: () => void): Promise<void> {
  return new Promise((resolve) => {
    if (typeof speechSynthesis === "undefined") {
      resolve();
      return;
    }
    speechSynthesis.cancel();
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.rate = 1.05;
    utterance.onstart = () => onStart?.();
    utterance.onend = () => resolve();
    utterance.onerror = () => resolve();
    speechSynthesis.speak(utterance);
  });
}

/** Stop talking immediately, for when somebody interrupts. */
export function stopSpeaking(): void {
  if (typeof speechSynthesis !== "undefined") {
    speechSynthesis.cancel();
  }
}

/**
 * The sound it makes when it hears its name.
 *
 * Local and immediate, before anything touches the network, because what makes a reply feel fast is
 * the time to the FIRST feedback rather than the time to the answer. Two rising notes for waking, one
 * falling note for going back to sleep.
 */
export function chirp(direction: "wake" | "sleep"): void {
  try {
    const context = new AudioContext();
    const now = context.currentTime;
    const notes = direction === "wake" ? [660, 880] : [660, 440];
    notes.forEach((frequency, index) => {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = "sine";
      oscillator.frequency.value = frequency;
      const at = now + index * 0.08;
      gain.gain.setValueAtTime(0, at);
      gain.gain.linearRampToValueAtTime(0.18, at + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.001, at + 0.09);
      oscillator.connect(gain).connect(context.destination);
      oscillator.start(at);
      oscillator.stop(at + 0.1);
    });
    window.setTimeout(() => void context.close(), 500);
  } catch {
    // A missing sound is a cosmetic loss, not a reason to fail a turn.
  }
}
