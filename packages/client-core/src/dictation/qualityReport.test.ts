import { describe, expect, it } from "vitest";
import { measureDictationQuality } from "./qualityReport";

const JABRA = { label: "Jabra Evolve2", deviceId: "id-jabra" };

// The background measurement runs on EVERY dictation, so what it declines to report matters as much
// as what it reports. Two clips that must never become data: one too short to measure honestly, and
// one with no speech in it - a false start or a moment of silence. Recording either would drag a
// device's averages down and eventually accuse a microphone that is fine.

const RATE = 48000;

/** Speech-like: syllables with quiet gaps, plus broadband content so the bandwidth check is honest. */
function speech(seconds: number, level = 0.3): Float32Array {
  const n = Math.round(RATE * seconds);
  const out = new Float32Array(n);
  const syl = Math.round(0.22 * RATE);
  const gap = Math.round(0.09 * RATE);
  let seed = 7;
  const rand = () => {
    seed = (seed * 1103515245 + 12345) & 0x7fffffff;
    return seed / 0x7fffffff;
  };
  for (let start = 0; start + syl < n; start += syl + gap) {
    for (let i = 0; i < syl; i++) {
      const env = 0.5 * (1 - Math.cos((2 * Math.PI * i) / (syl - 1)));
      const t = (start + i) / RATE;
      let v = 0;
      for (let h = 1; h * 120 < 3400; h++) v += Math.sin(2 * Math.PI * h * 120 * t) / h;
      out[start + i] = (v / 2.5 + (rand() * 2 - 1) * 0.05) * env * level;
    }
  }
  return out;
}

describe("measureDictationQuality", () => {
  it("measures an ordinary dictation and reports the device it came from", () => {
    const sample = measureDictationQuality(speech(10), RATE, JABRA, "dictation-send");

    expect(sample).not.toBeNull();
    expect(sample?.device).toBe("Jabra Evolve2");
    expect(sample?.deviceId).toBe("id-jabra");
    expect(["mobile", "mac", "windows", "unknown"]).toContain(sample?.platform);
    expect(sample?.platformRaw).toBeTruthy();
    expect(sample?.source).toBe("dictation-send");
    expect(sample?.sampleRate).toBe(RATE);
    expect(sample?.rating).toBeTruthy();
  });

  it("declines a clip too short to measure honestly", () => {
    // Under three seconds there are too few frames for a stable noise floor, and a shaky reading
    // reported as fact is worse than no reading.
    expect(measureDictationQuality(speech(1.5), RATE, JABRA, "dictation-send")).toBeNull();
  });

  it("declines a clip with no speech in it, rather than scoring silence as a bad microphone", () => {
    expect(measureDictationQuality(new Float32Array(RATE * 10), RATE, JABRA, "dictation-send")).toBeNull();
  });

  it("sends no audio and no transcript - only measurements and the device name", () => {
    const sample = measureDictationQuality(speech(10), RATE, JABRA, "dictation-send");
    const keys = Object.keys(sample ?? {}).sort();

    expect(keys).toEqual(
      [
        "clippedFraction",
        "device",
        "deviceId",
        "durationSeconds",
        "highBandRatioDb",
        "issues",
        "narrowband",
        "noiseFloorDb",
        "platform",
        "platformRaw",
        "rating",
        "sampleRate",
        "signalToNoiseDb",
        "source",
        "speechLevelDb",
      ].sort(),
    );
  });

  it("is JSON-safe even when a measurement is infinite", () => {
    // A digitally silent high band reads as -Infinity, which JSON.stringify turns into null and the
    // Gateway would then store as zero - a value that means "wideband", the opposite of the truth.
    const sample = measureDictationQuality(speech(10), RATE, JABRA, "dictation-send");
    const roundTripped = JSON.parse(JSON.stringify(sample)) as Record<string, unknown>;

    for (const [key, value] of Object.entries(roundTripped)) {
      expect(`${key}:${value === null ? "NULL" : "ok"}`).toBe(`${key}:ok`);
    }
    expect(Number.isFinite(sample?.highBandRatioDb)).toBe(true);
  });

  it("copes with an unnamed microphone", () => {
    // A browser withholds the label until permission has been granted at least once.
    expect(measureDictationQuality(speech(10), RATE, { label: "", deviceId: "" }, "dictation-send")?.device).toBe("");
  });
});
