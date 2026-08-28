// Does this device already have a speech recogniser we are allowed to use privately?
//
// Until Chrome 139 the browser's own recogniser sent audio to a server, which ruled it out for
// something that listens in a kitchen all day. On-device Web Speech changed that: the audio stays on
// the device, there is nothing to download, and the platform has already tuned it for exactly this
// job on exactly this battery. If it is present, most of the model machinery in this app is
// unnecessary weight.
//
// So this asks, per language, and reports what it is told. It does not infer availability from the
// browser version or the user agent, because "user-agent dependent" is what the specification
// actually says, and the whole lesson of the last two days is to measure rather than predict.

/** What the browser says about one language. The four states come from the specification. */
export type Availability = "available" | "downloadable" | "downloading" | "unavailable" | "not-supported" | "error";

export interface LanguageProbe {
  readonly language: string;
  /** With processLocally true: can it recognise this language without sending audio anywhere? */
  readonly onDevice: Availability;
  /** Without that flag: the ordinary, server-backed answer, for comparison. */
  readonly anywhere: Availability;
  readonly message?: string;
}

export interface PlatformSpeechProbe {
  readonly checkedAt: string;
  readonly userAgent: string;
  /** Is there a SpeechRecognition constructor at all? Without one, none of this is possible. */
  readonly hasRecogniser: boolean;
  /** Is it the prefixed, older one? Those predate on-device support. */
  readonly prefixed: boolean;
  /** Does the constructor carry the static availability query (Chrome 139 and later)? */
  readonly hasAvailabilityQuery: boolean;
  /** Does an instance accept the local-processing flag? */
  readonly acceptsProcessLocally: boolean;
  readonly languages: LanguageProbe[];
}

/** The languages worth asking about here: the English variants, and Danish. */
export const LANGUAGES = ["en-US", "en-GB", "en-CA", "da-DK"];

interface RecogniserConstructor {
  new (): { processLocally?: boolean };
  available?: (options: { langs: string[]; processLocally: boolean }) => Promise<string>;
  install?: (options: { langs: string[]; processLocally: boolean }) => Promise<boolean>;
}

function findConstructor(): { ctor: RecogniserConstructor | null; prefixed: boolean } {
  if (typeof window === "undefined") {
    return { ctor: null, prefixed: false };
  }
  const w = window as unknown as {
    SpeechRecognition?: RecogniserConstructor;
    webkitSpeechRecognition?: RecogniserConstructor;
  };
  if (w.SpeechRecognition !== undefined) {
    return { ctor: w.SpeechRecognition, prefixed: false };
  }
  if (w.webkitSpeechRecognition !== undefined) {
    return { ctor: w.webkitSpeechRecognition, prefixed: true };
  }
  return { ctor: null, prefixed: false };
}

/** Normalise whatever the browser returns into one of the states we understand. */
export function toAvailability(raw: unknown): Availability {
  if (raw === "available" || raw === "downloadable" || raw === "downloading" || raw === "unavailable") {
    return raw;
  }
  // Some builds answered the older boolean form of this question.
  if (raw === true) {
    return "available";
  }
  if (raw === false) {
    return "unavailable";
  }
  return "error";
}

/** Ask the browser what it can do. Never throws; a failure per language is recorded as one. */
export async function probePlatformSpeech(languages: string[] = LANGUAGES): Promise<PlatformSpeechProbe> {
  const { ctor, prefixed } = findConstructor();
  const hasAvailabilityQuery = ctor !== null && typeof ctor.available === "function";

  let acceptsProcessLocally = false;
  if (ctor !== null) {
    try {
      const instance = new ctor();
      instance.processLocally = true;
      acceptsProcessLocally = instance.processLocally === true;
    } catch {
      acceptsProcessLocally = false;
    }
  }

  const results: LanguageProbe[] = [];
  for (const language of languages) {
    if (ctor === null || !hasAvailabilityQuery) {
      results.push({
        language,
        onDevice: "not-supported",
        anywhere: ctor === null ? "not-supported" : "error",
        message: ctor === null
          ? "This browser has no speech recogniser at all."
          : "This browser has a recogniser but cannot be asked what it supports, so it predates on-device recognition.",
      });
      continue;
    }

    let onDevice: Availability = "error";
    let anywhere: Availability = "error";
    let message: string | undefined;
    try {
      onDevice = toAvailability(await ctor.available!({ langs: [language], processLocally: true }));
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
    }
    try {
      anywhere = toAvailability(await ctor.available!({ langs: [language], processLocally: false }));
    } catch (error) {
      message = message ?? (error instanceof Error ? error.message : String(error));
    }
    results.push({ language, onDevice, anywhere, message });
  }

  return {
    checkedAt: new Date().toISOString(),
    userAgent: typeof navigator === "undefined" ? "unknown" : navigator.userAgent,
    hasRecogniser: ctor !== null,
    prefixed,
    hasAvailabilityQuery,
    acceptsProcessLocally,
    languages: results,
  };
}

/**
 * Ask the browser to fetch the on-device model for a language.
 *
 * "downloadable" means it could work here but the model is not present yet. That is a very different
 * answer from "unavailable", and treating the two the same would write off a device that only needed
 * to be asked.
 */
export async function installLanguage(language: string): Promise<boolean> {
  const { ctor } = findConstructor();
  if (ctor === null || typeof ctor.install !== "function") {
    throw new Error("This browser cannot install on-device speech recognition.");
  }
  return ctor.install({ langs: [language], processLocally: true });
}

/** One sentence on what the probe means for this application. */
export function summarise(probe: PlatformSpeechProbe): string {
  if (!probe.hasRecogniser) {
    return "No speech recogniser in this browser. The platform cannot do the listening, so a model has to.";
  }
  if (!probe.hasAvailabilityQuery) {
    return "This browser has the old speech recogniser, which sends audio to a server. Not usable for something that listens all day.";
  }
  const usable = probe.languages.filter((l) => l.onDevice === "available");
  if (usable.length > 0) {
    return `On-device recognition is ready for ${usable.map((l) => l.language).join(", ")}. The platform can do the listening.`;
  }
  const later = probe.languages.filter((l) => l.onDevice === "downloadable" || l.onDevice === "downloading");
  if (later.length > 0) {
    return `On-device recognition is supported for ${later.map((l) => l.language).join(", ")} but the model is not here yet. Install it and check again.`;
  }
  return "The browser can be asked, and says no language works on-device here. A model has to do the listening on this device.";
}
