// Opening the microphone with the browser's own echo cancellation switched on.
//
// This is the single most important line of the whole application. Interrupting the assistant while
// it is talking, or asking it something while music is playing, only works when the microphone
// subtracts the sound the speakers are making. The browser has that built in and it is the same
// processing that makes a hands free telephone call work. Ask for it, then check that it was
// actually granted, because a constraint that is quietly ignored looks exactly like one that worked.

/** What the browser actually gave us, read back off the live audio track. */
export interface MicrophoneCapture {
  readonly stream: MediaStream;
  readonly deviceLabel: string;
  readonly echoCancellationEnabled: boolean;
  readonly noiseSuppressionEnabled: boolean;
  readonly automaticGainControlEnabled: boolean;
  readonly sampleRate: number | null;
  stop(): void;
}

/** Raised when the microphone cannot be opened. The message is written to be shown to a person. */
export class MicrophoneUnavailableError extends Error {
  constructor(message: string, readonly cause?: unknown) {
    super(message);
    this.name = "MicrophoneUnavailableError";
  }
}

/**
 * Open the default microphone with echo cancellation, noise suppression and automatic gain control.
 *
 * Throws rather than returning a degraded stream. A microphone opened without echo cancellation
 * cannot do the one thing this application exists to do, so quietly continuing with it would hide
 * the failure until somebody wondered why talking over the music never works.
 */
export async function openMicrophone(): Promise<MicrophoneCapture> {
  if (typeof navigator === "undefined" || navigator.mediaDevices === undefined) {
    throw new MicrophoneUnavailableError(
      "This browser does not expose microphone access. Open the page over a secure connection, meaning https or localhost.",
    );
  }

  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
      },
      video: false,
    });
  } catch (error) {
    throw new MicrophoneUnavailableError(describeGetUserMediaFailure(error), error);
  }

  const [track] = stream.getAudioTracks();
  if (track === undefined) {
    stream.getTracks().forEach((each) => each.stop());
    throw new MicrophoneUnavailableError("The browser returned a media stream with no audio track in it.");
  }

  // Read the settings the browser applied rather than the ones we asked for. Browsers are allowed to
  // ignore a constraint they cannot honour, and on some devices echo cancellation is one of them.
  const settings = track.getSettings();

  return {
    stream,
    deviceLabel: track.label.length > 0 ? track.label : "Unnamed microphone",
    echoCancellationEnabled: settings.echoCancellation === true,
    noiseSuppressionEnabled: settings.noiseSuppression === true,
    automaticGainControlEnabled: settings.autoGainControl === true,
    sampleRate: typeof settings.sampleRate === "number" ? settings.sampleRate : null,
    stop() {
      stream.getTracks().forEach((each) => each.stop());
    },
  };
}

/** Turn a getUserMedia rejection into something a person can act on. */
function describeGetUserMediaFailure(error: unknown): string {
  const name = error instanceof DOMException ? error.name : "";
  switch (name) {
    case "NotAllowedError":
      return "Microphone permission was refused. Allow the microphone for this page and try again.";
    case "NotFoundError":
      return "No microphone was found on this machine.";
    case "NotReadableError":
      return "The microphone is in use by another application and cannot be opened.";
    case "OverconstrainedError":
      return "No microphone on this machine supports echo cancellation, which this application needs.";
    default:
      return `The microphone could not be opened: ${error instanceof Error ? error.message : String(error)}`;
  }
}
