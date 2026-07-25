import { describe, expect, it } from "vitest";
import {
  analyzeMicQuality,
  averageSpectrum,
  checkMicQuality,
  fft,
  formatDb,
  judgeMicQuality,
  percentile,
} from "./micQuality";

// These tests are the evidence that the microphone check is worth shipping at all. The whole feature
// rests on one claim: that we can tell a genuinely bad capture from a good one WITHOUT guessing. So
// each defect is synthesized from its physical cause - a telephone-band link really is band-limited,
// a clipped signal really is flat-topped, a noisy room really does raise the floor between words -
// and the check must name that defect and, just as importantly, must stay silent on a good
// microphone. A check that cried wolf would be worse than no check, because the advice it gives
// ("replace your headset") costs the user real money.

// --- Signal synthesis ---------------------------------------------------------------------------

/** Deterministic PRNG so a threshold that sits too close to the edge fails every run, not one in ten. */
function mulberry32(seed: number): () => number {
  let a = seed;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** One RBJ-cookbook lowpass biquad pass (Q = 0.707). Cascaded to model a codec's steep band limit. */
function lowpassOnce(input: Float32Array, sampleRate: number, cutoffHz: number): Float32Array {
  const w0 = (2 * Math.PI * cutoffHz) / sampleRate;
  const alpha = Math.sin(w0) / (2 * 0.7071);
  const cosW0 = Math.cos(w0);
  const b0 = (1 - cosW0) / 2;
  const b1 = 1 - cosW0;
  const b2 = (1 - cosW0) / 2;
  const a0 = 1 + alpha;
  const a1 = -2 * cosW0;
  const a2 = 1 - alpha;

  const out = new Float32Array(input.length);
  let x1 = 0;
  let x2 = 0;
  let y1 = 0;
  let y2 = 0;
  for (let i = 0; i < input.length; i++) {
    const x0 = input[i];
    const y0 = (b0 / a0) * x0 + (b1 / a0) * x1 + (b2 / a0) * x2 - (a1 / a0) * y1 - (a2 / a0) * y2;
    out[i] = y0;
    x2 = x1;
    x1 = x0;
    y2 = y1;
    y1 = y0;
  }
  return out;
}

function lowpass(input: Float32Array, sampleRate: number, cutoffHz: number, passes: number): Float32Array {
  let out = input;
  for (let i = 0; i < passes; i++) out = lowpassOnce(out, sampleRate, cutoffHz);
  return out;
}

function highpass(input: Float32Array, sampleRate: number, cutoffHz: number): Float32Array {
  // Subtracting the lowpass leaves the high band - enough to place sibilant energy up top.
  const low = lowpass(input, sampleRate, cutoffHz, 2);
  const out = new Float32Array(input.length);
  for (let i = 0; i < input.length; i++) out[i] = input[i] - low[i];
  return out;
}

interface SpeechOptions {
  sampleRate: number;
  seconds: number;
  /** Peak amplitude of the voiced syllables, 0..1. */
  level: number;
  /** True for a real wideband microphone (sibilants present above 4.5 kHz). */
  wideband: boolean;
  /** Amplitude of the steady background noise. */
  noiseLevel?: number;
  /** Clip the finished signal at this absolute value (models an over-hot input). */
  clipAt?: number;
  seed?: number;
  /** Loudness of the fricatives relative to the voiced syllables. Lower models a duller microphone
   *  with weak sibilance - the hardest honest case for the narrowband check not to false-positive on. */
  sibilantGain?: number;
  /** Roll the whole signal off above this frequency (a muffled but genuinely wideband microphone). */
  dullAboveHz?: number;
}

/**
 * A speech-like signal: voiced syllables (a 120 Hz harmonic stack shaped by formants) separated by
 * quiet gaps, with fricative bursts every fourth syllable. Envelopes are raised-cosine because hard
 * gating would splatter broadband energy across the spectrum and fake the very high-frequency
 * content the narrowband check looks for.
 */
function synthSpeech(opts: SpeechOptions): Float32Array {
  const { sampleRate, seconds, level, wideband } = opts;
  const rand = mulberry32(opts.seed ?? 1);
  const n = Math.round(sampleRate * seconds);
  const out = new Float32Array(n);

  const syllableMs = 220;
  const gapMs = 90;
  const syllableLen = Math.round((syllableMs / 1000) * sampleRate);
  const gapLen = Math.round((gapMs / 1000) * sampleRate);
  const strideLen = syllableLen + gapLen;

  // Fricative source: broadband noise, high-passed so its energy sits where /s/ lives (4-9 kHz).
  const rawNoise = new Float32Array(n);
  for (let i = 0; i < n; i++) rawNoise[i] = rand() * 2 - 1;
  const sibilant = highpass(rawNoise, sampleRate, 4500);

  let syllable = 0;
  for (let start = 0; start + syllableLen < n; start += strideLen, syllable++) {
    const isFricative = syllable % 4 === 3;
    for (let i = 0; i < syllableLen; i++) {
      // Raised-cosine envelope: no hard edges, so no spectral splatter.
      const env = 0.5 * (1 - Math.cos((2 * Math.PI * i) / (syllableLen - 1)));
      const t = (start + i) / sampleRate;
      let sample: number;
      if (isFricative && wideband) {
        sample = sibilant[start + i] * (opts.sibilantGain ?? 1);
      } else if (isFricative) {
        // A telephone link turns /s/ into a dull low-frequency thud - no high content at all.
        sample = Math.sin(2 * Math.PI * 900 * t) * 0.5 + Math.sin(2 * Math.PI * 1500 * t) * 0.3;
      } else {
        // Voiced: harmonics of 120 Hz with a 1/n rolloff, all inside the speech band on both
        // wideband and narrowband captures (real voiced speech has little energy above 3.4 kHz).
        sample = 0;
        for (let h = 1; h * 120 < 3400; h++) {
          sample += Math.sin(2 * Math.PI * h * 120 * t) / h;
        }
        sample /= 2.5;
      }
      out[start + i] += sample * env * level;
    }
  }

  if (opts.noiseLevel && opts.noiseLevel > 0) {
    // The background comes through the SAME link as the voice, so a narrowband capture has
    // band-limited noise too. Modelling it any other way would hand the check a free high-band clue.
    const bg = new Float32Array(n);
    for (let i = 0; i < n; i++) bg[i] = rand() * 2 - 1;
    const shaped = wideband ? bg : lowpass(bg, sampleRate, 3400, 8);
    for (let i = 0; i < n; i++) out[i] += shaped[i] * opts.noiseLevel;
  }

  // A narrowband link band-limits EVERYTHING it carries. Eight cascaded poles at 3.6 kHz is the
  // steep cliff a real 8 kHz codec leaves behind. A merely DULL microphone gets a gentle rolloff
  // instead - it still carries high content, just less of it.
  const banded = wideband
    ? opts.dullAboveHz
      ? lowpass(out, sampleRate, opts.dullAboveHz, 2)
      : out
    : lowpass(out, sampleRate, 3600, 8);

  if (opts.clipAt !== undefined) {
    for (let i = 0; i < banded.length; i++) {
      banded[i] = Math.max(-opts.clipAt, Math.min(opts.clipAt, banded[i]));
    }
    // A real clip pins samples AT the rail; normalise so the flat tops land at full scale.
    for (let i = 0; i < banded.length; i++) banded[i] /= opts.clipAt;
  }
  return banded;
}

const RATE = 48000;

// --- The building blocks ------------------------------------------------------------------------

describe("fft", () => {
  it("puts a pure tone in the bin that matches its frequency", () => {
    const size = 1024;
    const rate = 48000;
    const toneHz = 3000;
    const re = new Float64Array(size);
    const im = new Float64Array(size);
    for (let i = 0; i < size; i++) re[i] = Math.sin((2 * Math.PI * toneHz * i) / rate);
    fft(re, im);

    let peakBin = 0;
    let peak = 0;
    for (let bin = 1; bin < size / 2; bin++) {
      const power = re[bin] * re[bin] + im[bin] * im[bin];
      if (power > peak) {
        peak = power;
        peakBin = bin;
      }
    }
    expect(peakBin * (rate / size)).toBeCloseTo(toneHz, -2);
  });
});

describe("percentile", () => {
  it("reads the value at the requested point of a sorted list", () => {
    const sorted = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
    expect(percentile(sorted, 0)).toBe(0);
    expect(percentile(sorted, 1)).toBe(9);
    expect(percentile(sorted, 0.5)).toBeGreaterThanOrEqual(4);
    expect(percentile(sorted, 0.5)).toBeLessThanOrEqual(5);
  });

  it("is 0 for an empty list rather than throwing", () => {
    expect(percentile([], 0.5)).toBe(0);
  });
});

describe("averageSpectrum", () => {
  it("returns an all-zero spectrum when there are no usable frames", () => {
    const power = averageSpectrum(new Float32Array(100), []);
    expect(power.every((p) => p === 0)).toBe(true);
  });
});

// --- The measurement ----------------------------------------------------------------------------

describe("analyzeMicQuality", () => {
  it("measures a healthy wideband microphone as good, with no issues raised", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: true, noiseLevel: 0.002 });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.narrowband).toBe(false);
    expect(report.clippedFraction).toBeLessThan(0.001);
    expect(report.signalToNoiseDb).toBeGreaterThan(18);
    expect(report.speechLevelDb).toBeGreaterThan(-32);
  });

  it("detects a telephone-band capture (the Bluetooth hands-free headset)", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: false, noiseLevel: 0.002 });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.narrowband).toBe(true);
  });

  it("does NOT flag a dull microphone with weak sibilance as narrowband", () => {
    // The false positive that would matter most: telling someone with a perfectly usable microphone
    // to go and buy a new headset. A dull capture still carries real high-frequency content, just
    // less of it, and the check must see the difference between "quiet up there" and "nothing there".
    const samples = synthSpeech({
      sampleRate: RATE,
      seconds: 4,
      level: 0.3,
      wideband: true,
      noiseLevel: 0.002,
      sibilantGain: 0.1,
    });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.narrowband).toBe(false);
    // And with real margin, not by a hair - the decision must not be knife-edge.
    expect(report.highBandRatioDb).toBeGreaterThan(-40);
  });

  it("does NOT flag a muffled but genuinely wideband microphone as narrowband", () => {
    const samples = synthSpeech({
      sampleRate: RATE,
      seconds: 4,
      level: 0.3,
      wideband: true,
      noiseLevel: 0.002,
      sibilantGain: 0.3,
      dullAboveHz: 5500,
    });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.narrowband).toBe(false);
  });

  it("separates wideband from narrowband by a wide margin, not a hair", () => {
    // The evidence that the narrowband threshold is safe: the two populations are not adjacent, they
    // are tens of decibels apart, and -45 dB sits in the empty gap between them.
    const wide = analyzeMicQuality(
      synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: true, noiseLevel: 0.002, sibilantGain: 0.1 }),
      RATE,
    );
    const narrow = analyzeMicQuality(
      synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: false, noiseLevel: 0.002 }),
      RATE,
    );
    expect(wide.highBandRatioDb - narrow.highBandRatioDb).toBeGreaterThan(40);
  });

  it("calls a device that cannot even deliver the band narrowband outright", () => {
    // An 8 kHz stream has a 4 kHz Nyquist - there is no high band to inspect, it IS the defect.
    const samples = synthSpeech({ sampleRate: 8000, seconds: 4, level: 0.3, wideband: false, noiseLevel: 0.002 });
    const report = analyzeMicQuality(samples, 8000);
    expect(report.narrowband).toBe(true);
  });

  it("measures clipping on an over-hot input", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.9, wideband: true, noiseLevel: 0.002, clipAt: 0.35 });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.clippedFraction).toBeGreaterThan(0.01);
  });

  it("measures a quiet microphone as quiet without inventing other faults", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.004, wideband: true, noiseLevel: 0.00002 });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.speechLevelDb).toBeLessThan(-42);
    expect(report.clippedFraction).toBe(0);
  });

  it("measures a poor signal-to-noise ratio when the room is loud", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.25, wideband: true, noiseLevel: 0.1 });
    const report = analyzeMicQuality(samples, RATE);

    expect(report.heardSpeech).toBe(true);
    expect(report.signalToNoiseDb).toBeLessThan(18);
  });

  it("reports no speech for digital silence", () => {
    const report = analyzeMicQuality(new Float32Array(RATE * 3), RATE);
    expect(report.heardSpeech).toBe(false);
  });

  it("reports no speech for a clip that is nothing but steady hiss", () => {
    const rand = mulberry32(7);
    const samples = new Float32Array(RATE * 3);
    for (let i = 0; i < samples.length; i++) samples[i] = (rand() * 2 - 1) * 0.05;
    const report = analyzeMicQuality(samples, RATE);
    // Steady noise has no loud end standing above its quiet end - there is no voice here.
    expect(report.heardSpeech).toBe(false);
  });

  it("does not throw on a clip too short to hold a single frame", () => {
    const report = analyzeMicQuality(new Float32Array(4), RATE);
    expect(report.heardSpeech).toBe(false);
    expect(report.durationSeconds).toBeGreaterThan(0);
  });
});

// --- The verdict --------------------------------------------------------------------------------

describe("judgeMicQuality", () => {
  it("passes a good wideband microphone silently - no advice, nothing to fix", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: true, noiseLevel: 0.002 });
    const verdict = checkMicQuality(samples, RATE);

    expect(verdict.rating).toBe("good");
    expect(verdict.issues).toEqual([]);
    expect(verdict.headline).toContain("sounds good");
  });

  it("names the Bluetooth hands-free link as the problem and says how to fix it", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.3, wideband: false, noiseLevel: 0.002 });
    const verdict = checkMicQuality(samples, RATE);

    expect(verdict.rating).toBe("poor");
    const narrow = verdict.issues.find((i) => i.id === "narrowband");
    expect(narrow).toBeDefined();
    expect(narrow?.advice).toContain("Bluetooth");
  });

  it("reports clipping with the measured percentage in the title", () => {
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.9, wideband: true, noiseLevel: 0.002, clipAt: 0.35 });
    const verdict = checkMicQuality(samples, RATE);

    const clipping = verdict.issues.find((i) => i.id === "clipping");
    expect(clipping).toBeDefined();
    expect(clipping?.severity).toBe("poor");
    expect(clipping?.title).toMatch(/%/);
  });

  it("tells the user we heard nothing rather than scoring an empty clip", () => {
    const verdict = checkMicQuality(new Float32Array(RATE * 3), RATE);

    expect(verdict.rating).toBe("poor");
    expect(verdict.issues).toHaveLength(1);
    expect(verdict.issues[0].id).toBe("no-speech");
    // It must NOT claim the microphone is narrowband or clipping on the strength of silence.
    expect(verdict.issues.some((i) => i.id === "narrowband")).toBe(false);
  });

  it("puts the most damaging problem first", () => {
    // Quiet AND telephone-band: the band limit is what actually breaks transcription.
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.005, wideband: false, noiseLevel: 0.00002 });
    const verdict = checkMicQuality(samples, RATE);

    expect(verdict.issues.length).toBeGreaterThan(1);
    expect(verdict.issues[0].severity).toBe("poor");
  });

  it("grades a merely imperfect microphone fair, not poor - a good mic is never nagged", () => {
    // Wideband and undistorted, just a little hissy: worth a note, not an alarm.
    const samples = synthSpeech({ sampleRate: RATE, seconds: 4, level: 0.25, wideband: true, noiseLevel: 0.05 });
    const verdict = checkMicQuality(samples, RATE);

    expect(verdict.report.narrowband).toBe(false);
    expect(verdict.rating).toBe("fair");
    expect(verdict.issues.every((i) => i.severity === "fair")).toBe(true);
  });
});

describe("judgeMicQuality thresholds", () => {
  // The fold pinned directly against hand-built measurements, independent of the synthesizer: these
  // fix WHERE each threshold sits, so a later tweak to the analysis cannot quietly move the point at
  // which a user starts being told their microphone is bad.
  const healthy = {
    heardSpeech: true,
    durationSeconds: 4,
    sampleRate: 48000,
    speechLevelDb: -20,
    noiseFloorDb: -55,
    signalToNoiseDb: 35,
    clippedFraction: 0,
    highBandRatioDb: -25,
    narrowband: false,
  };

  it("passes a healthy report with no issues", () => {
    expect(judgeMicQuality(healthy).rating).toBe("good");
  });

  it("stays silent just inside every threshold and speaks up just outside", () => {
    expect(judgeMicQuality({ ...healthy, signalToNoiseDb: 18 }).issues).toEqual([]);
    expect(judgeMicQuality({ ...healthy, signalToNoiseDb: 17 }).issues[0].id).toBe("noisy");

    expect(judgeMicQuality({ ...healthy, speechLevelDb: -32 }).issues).toEqual([]);
    expect(judgeMicQuality({ ...healthy, speechLevelDb: -33 }).issues[0].id).toBe("too-quiet");

    expect(judgeMicQuality({ ...healthy, clippedFraction: 0.0009 }).issues).toEqual([]);
    expect(judgeMicQuality({ ...healthy, clippedFraction: 0.0011 }).issues[0].id).toBe("clipping");
  });

  it("escalates from fair to poor at the severe end of each threshold", () => {
    expect(judgeMicQuality({ ...healthy, signalToNoiseDb: 12 }).rating).toBe("fair");
    expect(judgeMicQuality({ ...healthy, signalToNoiseDb: 9 }).rating).toBe("poor");

    expect(judgeMicQuality({ ...healthy, speechLevelDb: -38 }).rating).toBe("fair");
    expect(judgeMicQuality({ ...healthy, speechLevelDb: -45 }).rating).toBe("poor");
  });

  it("treats a narrowband link as poor on its own, however clean the rest is", () => {
    const verdict = judgeMicQuality({ ...healthy, narrowband: true, highBandRatioDb: -110 });
    expect(verdict.rating).toBe("poor");
    expect(verdict.issues).toHaveLength(1);
    expect(verdict.issues[0].id).toBe("narrowband");
  });
});

describe("formatDb", () => {
  it("renders a level and guards the silent case", () => {
    expect(formatDb(-20.4)).toBe("-20 dB");
    expect(formatDb(-Infinity)).toBe("--");
  });
});
