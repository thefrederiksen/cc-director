// Which KIND of machine a dictation was captured on (issue #2183): the report should be able to
// say "your phone microphone beats your Windows headset", not list two names with no context.
//
// The buckets are deliberately few - mobile, mac, windows - because that is the comparison the
// owner actually makes; anything else is "unknown" rather than a guessed bucket. The RAW evidence
// travels alongside the bucket so a wrong classification can be diagnosed later without guessing,
// and it is capped because it rides on every quality sample.

/** The classification buckets. "unknown" means undeterminable, never "probably windows". */
export type PlatformBucket = "mobile" | "mac" | "windows" | "unknown";

export interface PlatformClassification {
  platform: PlatformBucket;
  /** The raw evidence the bucket was derived from, for diagnosing a wrong bucket later. */
  platformRaw: string;
}

/** The slice of the navigator the classifier reads. A plain interface so tests need no browser. */
export interface NavigatorHints {
  /** navigator.userAgentData, when the browser has it (Chromium). */
  userAgentData?: { platform?: string; mobile?: boolean };
  userAgent?: string;
  platform?: string;
  /** navigator.maxTouchPoints - how iPadOS Safari is told apart from a real Mac. */
  maxTouchPoints?: number;
}

const RAW_CAP = 160;

function cap(value: string): string {
  return value.length <= RAW_CAP ? value : value.slice(0, RAW_CAP);
}

/**
 * Classify the platform from navigator hints. Pure so it can be tested without a browser.
 *
 * Client-Hints first (Chromium ships exact strings there and they need no permission), then the
 * user agent string, in honesty order: mobile signals are checked before desktop ones because a
 * phone's user agent CONTAINS desktop tokens ("like Mac OS X") and the reverse is never true.
 */
export function classifyPlatform(hints: NavigatorHints): PlatformClassification {
  const uaData = hints.userAgentData;
  if (uaData !== undefined && (uaData.platform !== undefined || uaData.mobile !== undefined)) {
    const raw = cap(`uaData platform=${uaData.platform ?? ""} mobile=${uaData.mobile === true}`);
    if (uaData.mobile === true) return { platform: "mobile", platformRaw: raw };
    switch (uaData.platform) {
      case "Android":
      case "iOS":
        return { platform: "mobile", platformRaw: raw };
      case "macOS":
        return { platform: "mac", platformRaw: raw };
      case "Windows":
        return { platform: "windows", platformRaw: raw };
      default:
        return { platform: "unknown", platformRaw: raw };
    }
  }

  const ua = hints.userAgent ?? "";
  const navPlatform = hints.platform ?? "";
  const touch = hints.maxTouchPoints ?? 0;
  const raw = cap(`ua=${ua} platform=${navPlatform} touch=${touch}`);

  if (/iPhone|iPad|iPod|Android/i.test(ua)) return { platform: "mobile", platformRaw: raw };
  // iPadOS Safari masquerades as a Mac; a "Mac" with a multi-touch screen is an iPad.
  if (/Mac/i.test(navPlatform) && touch > 1) return { platform: "mobile", platformRaw: raw };
  if (/Windows/i.test(ua) || /^Win/i.test(navPlatform)) return { platform: "windows", platformRaw: raw };
  if (/Mac/i.test(ua) || /^Mac/i.test(navPlatform)) return { platform: "mac", platformRaw: raw };
  return { platform: "unknown", platformRaw: raw };
}

/** Classify the platform this code is running on, from the real navigator. */
export function currentPlatform(): PlatformClassification {
  if (typeof navigator === "undefined") return { platform: "unknown", platformRaw: "no navigator" };
  const nav = navigator as Navigator & { userAgentData?: { platform?: string; mobile?: boolean } };
  return classifyPlatform({
    userAgentData: nav.userAgentData,
    userAgent: nav.userAgent,
    platform: nav.platform,
    maxTouchPoints: nav.maxTouchPoints,
  });
}
