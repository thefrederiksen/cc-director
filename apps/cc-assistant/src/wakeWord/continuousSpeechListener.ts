// Continuous speech recognition, which is what makes a wake word each person chooses possible.
//
// The alternative approach is a small model trained on one fixed phrase. Those are cheaper to run
// and more accurate, but the phrase has to be chosen and the model built before the application
// ships, so nobody can pick their own word. Recognising everything and looking for the chosen word
// in the text costs more, and buys the thing we actually want.
//
// This wrapper exists because the browser interface is awkward in three specific ways: it stops on
// its own every minute or so, it reports ordinary silence as an error, and it delivers each utterance
// as a growing list rather than a single result.

/** One piece of transcript, either still being revised or settled. */
export interface SpeechFragment {
  readonly text: string;
  /** False while the person is still speaking this utterance and the words may still change. */
  readonly isFinal: boolean;
  /** Rises by one for every utterance. Lets a caller fire on a wake word once per utterance. */
  readonly utteranceNumber: number;
}

export interface ContinuousSpeechListenerEvents {
  onFragment(fragment: SpeechFragment): void;
  /** A condition worth showing but not worth stopping for, such as a stretch of silence. */
  onNotice(message: string): void;
  /** Listening has stopped and will not resume by itself. */
  onFatalError(message: string): void;
}

/** Raised when the browser has no speech recognition at all. */
export class SpeechRecognitionUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SpeechRecognitionUnavailableError";
  }
}

export interface ContinuousSpeechListener {
  start(): void;
  stop(): void;
  readonly isListening: boolean;
}

/**
 * Start recognising speech continuously until stop is called.
 *
 * The language matters more than it looks. Recognition accuracy on an unusual wake word depends on
 * the recogniser expecting the right language, so this is exposed rather than assumed.
 */
export function createContinuousSpeechListener(
  language: string,
  events: ContinuousSpeechListenerEvents,
): ContinuousSpeechListener {
  const foundConstructor = findSpeechRecognitionConstructor();
  if (foundConstructor === null) {
    throw new SpeechRecognitionUnavailableError(
      "This browser has no speech recognition. Chrome and Edge on the desktop both have it; Firefox does not.",
    );
  }
  // Held in its own constant so the closures below see a value that cannot be null, rather than
  // relying on narrowing that does not survive into a nested function.
  const RecognitionConstructor: SpeechRecognitionConstructorLike = foundConstructor;

  let recognition: SpeechRecognitionLike | null = null;
  let wantsToListen = false;
  let utteranceNumber = 0;
  let consecutiveImmediateStops = 0;
  let lastStartedAt = 0;

  function attach(): SpeechRecognitionLike {
    const created = new RecognitionConstructor();
    created.lang = language;
    created.continuous = true;
    created.interimResults = true;
    created.maxAlternatives = 1;

    created.onresult = (event: SpeechRecognitionEventLike) => {
      for (let index = event.resultIndex; index < event.results.length; index += 1) {
        const result = event.results[index];
        const alternative = result[0];
        if (alternative === undefined) {
          continue;
        }
        events.onFragment({
          text: alternative.transcript,
          isFinal: result.isFinal,
          utteranceNumber: utteranceNumber + index,
        });
      }
    };

    created.onerror = (event: SpeechRecognitionErrorEventLike) => {
      switch (event.error) {
        // Silence and a deliberate restart are normal operation, not failures.
        case "no-speech":
        case "aborted":
          return;
        case "not-allowed":
        case "service-not-allowed":
          wantsToListen = false;
          events.onFatalError(
            "Microphone permission was refused for speech recognition. Allow the microphone for this page and start again.",
          );
          return;
        case "audio-capture":
          wantsToListen = false;
          events.onFatalError("No microphone could be read while recognising speech.");
          return;
        case "network":
          events.onNotice("The speech recognition service could not be reached. Retrying.");
          return;
        default:
          events.onNotice(`Speech recognition reported: ${event.error}`);
      }
    };

    // Browsers end a continuous session on their own after roughly a minute, and after every long
    // silence. Restarting is normal operation. Restarting instantly and repeatedly is not, and means
    // something is wrong that a restart will never fix, so that case stops and says so.
    created.onend = () => {
      if (!wantsToListen) {
        return;
      }
      const ranFor = Date.now() - lastStartedAt;
      if (ranFor < 250) {
        consecutiveImmediateStops += 1;
      } else {
        consecutiveImmediateStops = 0;
      }
      if (consecutiveImmediateStops >= 5) {
        wantsToListen = false;
        events.onFatalError(
          "Speech recognition stopped immediately five times in a row. It is not able to run on this machine right now.",
        );
        return;
      }
      utteranceNumber += 1000;
      begin();
    };

    return created;
  }

  function begin(): void {
    recognition = attach();
    lastStartedAt = Date.now();
    try {
      recognition.start();
    } catch (error) {
      wantsToListen = false;
      events.onFatalError(
        `Speech recognition would not start: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }

  return {
    start() {
      if (wantsToListen) {
        return;
      }
      wantsToListen = true;
      consecutiveImmediateStops = 0;
      begin();
    },
    stop() {
      wantsToListen = false;
      if (recognition !== null) {
        recognition.onend = null;
        recognition.stop();
        recognition = null;
      }
    },
    get isListening() {
      return wantsToListen;
    },
  };
}

// The browser interface, described only as far as this file uses it. Written out here rather than
// pulled in as a dependency, because it is twenty lines and the shape has been stable for years.

interface SpeechRecognitionAlternativeLike {
  readonly transcript: string;
}
interface SpeechRecognitionResultLike {
  readonly isFinal: boolean;
  readonly length: number;
  [index: number]: SpeechRecognitionAlternativeLike | undefined;
}
interface SpeechRecognitionResultListLike {
  readonly length: number;
  [index: number]: SpeechRecognitionResultLike;
}
interface SpeechRecognitionEventLike {
  readonly resultIndex: number;
  readonly results: SpeechRecognitionResultListLike;
}
interface SpeechRecognitionErrorEventLike {
  readonly error: string;
}
interface SpeechRecognitionLike {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  maxAlternatives: number;
  onresult: ((event: SpeechRecognitionEventLike) => void) | null;
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
}
type SpeechRecognitionConstructorLike = new () => SpeechRecognitionLike;

function findSpeechRecognitionConstructor(): SpeechRecognitionConstructorLike | null {
  if (typeof window === "undefined") {
    return null;
  }
  const candidate = window as unknown as {
    SpeechRecognition?: SpeechRecognitionConstructorLike;
    webkitSpeechRecognition?: SpeechRecognitionConstructorLike;
  };
  return candidate.SpeechRecognition ?? candidate.webkitSpeechRecognition ?? null;
}
