// Microphone quality measurement for the "Test microphone" check on the Cockpit dictation health
// screen and the mobile app.
//
// WHY THIS EXISTS: bad transcription is usually a bad MICROPHONE, not a bad model. A headset on a
// Bluetooth hands-free link delivers 8 kHz phone-quality audio; a gain-staged-too-hot mic clips; a
// laptop mic across the room buries the voice in room noise. All three produce a transcript full of
// nonsense and none of them are visible to the user, who only knows "dictation is rubbish". This
// module turns a recorded clip into MEASUREMENTS of those three specific defects.
//
// WHAT THIS DELIBERATELY DOES NOT DO: it does not score "audio quality" as a single opinion, and it
// does not guess. Every number below is a physical measurement of the samples with an unambiguous
// meaning, and each one maps to a defect the user can actually act on. Anything we could not measure
// honestly (is the speaker mumbling? is the accent hard? is the room reverberant?) is absent rather
// than estimated, because a confident wrong diagnosis is worse than no diagnosis - it sends the user
// to replace a microphone that was fine.
//
// ON THE "CLIENT IS DUMB" RULE (CLAUDE.md #7): that rule puts every SESSION display verdict on the
// Gateway so two clients cannot invent different meanings for the same pushed state. This check is
// local-only by design - the audio never leaves the device, and shipping it to the Gateway just to
// be ruled on would be a privacy regression for zero benefit. The rule's actual principle - ONE
// place folds the verdict, clients render it verbatim - is honoured here: the fold is this shared
// module, and both the Cockpit and the mobile app render the strings it returns without re-deciding
// anything. Adding a new defect is one edit here, not a branch in each client.

// Analysis frame. 20 ms is the standard short-time window for speech: long enough for a stable
// energy reading, short enough that a frame is either speech or gap rather than a blur of both.
const FRAME_MS = 20;

// A frame this far above the measured noise floor is counted as speech. 6 dB is 2x the noise
// amplitude - comfortably above floor wobble, low enough to keep quiet syllables.
const SPEECH_OVER_FLOOR_DB = 6;

// |sample| at or above this is treated as pinned to the rail. Not exactly 1.0: integer-encoded audio
// that has been resampled lands a hair under full scale, and a true clip is flat-topped regardless.
const CLIP_CEILING = 0.98;

// Speech band used for every relative-energy comparison: the range that actually carries
// intelligibility, and the band a telephone link preserves.
const SPEECH_BAND_LOW_HZ = 300;
const SPEECH_BAND_HIGH_HZ = 3400;

// The band that only a genuinely wideband capture can contain. A narrowband (8 kHz) source upsampled
// to 48 kHz has essentially NOTHING here - the resampler's stopband, tens of dB below anything real.
const HIGH_BAND_LOW_HZ = 4500;
const HIGH_BAND_HIGH_HZ = 8000;

// --- Thresholds -------------------------------------------------------------------------------
// Each is a cliff in how the audio BEHAVES, not a taste preference, and each is set where the
// evidence is unambiguous so a good microphone is never nagged.

// Real speech through a wideband microphone always puts SOME energy at 4.5-8 kHz (sibilants, breath,
// room air) - typically 20-35 dB below the speech band. A narrowband link upsampled to 48 kHz leaves
// the resampler's stopband there instead, 50+ dB down. -45 dB sits in the empty gap between those two
// populations, so this fires on a genuine cliff and not on a merely dull-sounding microphone.
const NARROWBAND_RATIO_DB = -45;

// Clipping is flat-topped distortion; the transcriber hears a buzz over the vowels. A stray sample or
// two at the rail is normal, a percent of them is not.
const CLIP_POOR = 0.01;
const CLIP_FAIR = 0.001;

// Whisper degrades sharply once the voice stops standing clear of the background.
const SNR_POOR_DB = 10;
const SNR_FAIR_DB = 18;

// Below this the voice is so far down that the encoder spends its bits on noise.
const LEVEL_POOR_DB = -42;
const LEVEL_FAIR_DB = -32;

// 16-bit audio quantises at about -96 dBFS, so a "noise floor" below this is digital silence rather
// than a measurement of a room. Clamping here keeps the signal-to-noise ratio a finite number: an
// unclamped floor of zero makes it Infinity, which is not a reading anyone can act on and which
// would propagate through the verdict and out to the screen.
const MIN_MEASURABLE_DB = -100;

export type QualityRating = "good" | "fair" | "poor";

export interface MicQualityReport {
  /** Whether the clip contained any speech at all. Everything below is meaningless when false. */
  heardSpeech: boolean;
  /** Duration of the analysed audio. */
  durationSeconds: number;
  /** Sample rate the microphone actually delivered. */
  sampleRate: number;
  /** Typical loudness of the speech itself, in dBFS (0 = full scale). Around -20 is healthy. */
  speechLevelDb: number;
  /** Loudness of the quiet gaps between words, in dBFS. The room + the microphone's own hiss. */
  noiseFloorDb: number;
  /** How far the voice stands above the background, in dB. The single best predictor of whether
   *  transcription will work. */
  signalToNoiseDb: number;
  /** Fraction of samples pinned to full scale (0..1). Anything above a trace is audible distortion. */
  clippedFraction: number;
  /** Energy in the 4.5-8 kHz band relative to the speech band, in dB. A hard cliff here is the
   *  signature of a Bluetooth hands-free / telephone-grade link. -Infinity when nothing is there. */
  highBandRatioDb: number;
  /** True when the capture carries no real content above the telephone band. */
  narrowband: boolean;
}

/** One named thing that is wrong with the capture, in the user's words, with what to do about it. */
export interface MicQualityIssue {
  /** Stable identifier, for tests and telemetry. Never shown to the user. */
  id: "no-speech" | "narrowband" | "clipping" | "too-quiet" | "noisy";
  severity: "poor" | "fair";
  /** What was measured, in plain English. */
  title: string;
  /** What the user should change. */
  advice: string;
}

/** The finished verdict both clients render verbatim. */
export interface MicQualityVerdict {
  rating: QualityRating;
  /** One-line headline for the result banner. */
  headline: string;
  /** Ordered worst-first. Empty when the microphone is good. */
  issues: MicQualityIssue[];
  report: MicQualityReport;
}

function toDb(amplitude: number): number {
  if (amplitude <= 0) return -Infinity;
  return 20 * Math.log10(amplitude);
}

/** Value at a percentile (0..1) of a numeric list. Used to read the noise floor and the speech level
 *  off the distribution of frame energies rather than off the min/max, which single clicks and pops
 *  would otherwise dominate. */
export function percentile(sorted: number[], p: number): number {
  if (sorted.length === 0) return 0;
  const index = Math.min(sorted.length - 1, Math.max(0, Math.round(p * (sorted.length - 1))));
  return sorted[index];
}

/** Root-mean-square amplitude of a slice of samples. */
function rms(samples: Float32Array, from: number, to: number): number {
  let sum = 0;
  for (let i = from; i < to; i++) sum += samples[i] * samples[i];
  const n = to - from;
  return n > 0 ? Math.sqrt(sum / n) : 0;
}

// --- Fast Fourier transform (iterative radix-2, in place) ------------------------------------
// A compact FFT so the bandwidth check needs no dependency. re/im are power-of-two length.
export function fft(re: Float64Array, im: Float64Array): void {
  const n = re.length;
  if (n <= 1) return;

  // Bit-reversal permutation.
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) {
      [re[i], re[j]] = [re[j], re[i]];
      [im[i], im[j]] = [im[j], im[i]];
    }
  }

  for (let len = 2; len <= n; len <<= 1) {
    const angle = (-2 * Math.PI) / len;
    const wRe = Math.cos(angle);
    const wIm = Math.sin(angle);
    for (let i = 0; i < n; i += len) {
      let curRe = 1;
      let curIm = 0;
      for (let k = 0; k < len / 2; k++) {
        const aRe = re[i + k];
        const aIm = im[i + k];
        const bRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
        const bIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
        re[i + k] = aRe + bRe;
        im[i + k] = aIm + bIm;
        re[i + k + len / 2] = aRe - bRe;
        im[i + k + len / 2] = aIm - bIm;
        const nextRe = curRe * wRe - curIm * wIm;
        curIm = curRe * wIm + curIm * wRe;
        curRe = nextRe;
      }
    }
  }
}

// Spectrum window for the bandwidth check. 1024 bins at 48 kHz is ~47 Hz resolution over a 21 ms
// window - fine enough to place the telephone cliff at 3.4-4 kHz precisely.
const SPECTRUM_SIZE = 1024;

/**
 * Average power spectrum across the given frame start offsets, Hann-windowed. Returns power per bin
 * for bins 0..SPECTRUM_SIZE/2 (the real half of the spectrum).
 */
export function averageSpectrum(samples: Float32Array, frameStarts: number[]): Float64Array {
  const half = SPECTRUM_SIZE / 2;
  const power = new Float64Array(half + 1);
  if (frameStarts.length === 0) return power;

  // Hann window, precomputed once.
  const window = new Float64Array(SPECTRUM_SIZE);
  for (let i = 0; i < SPECTRUM_SIZE; i++) {
    window[i] = 0.5 * (1 - Math.cos((2 * Math.PI * i) / (SPECTRUM_SIZE - 1)));
  }

  const re = new Float64Array(SPECTRUM_SIZE);
  const im = new Float64Array(SPECTRUM_SIZE);
  let used = 0;

  for (const start of frameStarts) {
    if (start + SPECTRUM_SIZE > samples.length) continue;
    for (let i = 0; i < SPECTRUM_SIZE; i++) {
      re[i] = samples[start + i] * window[i];
      im[i] = 0;
    }
    fft(re, im);
    for (let bin = 0; bin <= half; bin++) {
      power[bin] += re[bin] * re[bin] + im[bin] * im[bin];
    }
    used++;
  }

  if (used > 0) {
    for (let bin = 0; bin <= half; bin++) power[bin] /= used;
  }
  return power;
}

/** Sum the power spectrum over a frequency range. */
function bandEnergy(power: Float64Array, sampleRate: number, lowHz: number, highHz: number): number {
  const binHz = sampleRate / SPECTRUM_SIZE;
  const from = Math.max(1, Math.floor(lowHz / binHz));
  const to = Math.min(power.length - 1, Math.ceil(highHz / binHz));
  let sum = 0;
  for (let bin = from; bin <= to; bin++) sum += power[bin];
  return sum;
}

/**
 * Measure a recorded clip. `samples` must be mono at the microphone's NATIVE sample rate - measuring
 * a clip that has already been resampled to 16 kHz would destroy the very high-frequency evidence
 * the narrowband check reads.
 */
export function analyzeMicQuality(samples: Float32Array, sampleRate: number): MicQualityReport {
  const frameSize = Math.max(1, Math.round((FRAME_MS / 1000) * sampleRate));
  const durationSeconds = samples.length / sampleRate;

  // Per-frame energy, which everything else is read off.
  const frameLevels: number[] = [];
  const frameStarts: number[] = [];
  for (let start = 0; start + frameSize <= samples.length; start += frameSize) {
    frameLevels.push(rms(samples, start, start + frameSize));
    frameStarts.push(start);
  }

  const empty: MicQualityReport = {
    heardSpeech: false,
    durationSeconds,
    sampleRate,
    speechLevelDb: -Infinity,
    noiseFloorDb: -Infinity,
    signalToNoiseDb: 0,
    clippedFraction: 0,
    highBandRatioDb: -Infinity,
    narrowband: false,
  };
  if (frameLevels.length === 0) return empty;

  const sortedLevels = [...frameLevels].sort((a, b) => a - b);
  // The noise floor is the quiet end of the distribution (the gaps between words); the speech level
  // is the loud end (the vowels). Percentiles, not min/max, so one door slam cannot define either.
  const noiseFloor = percentile(sortedLevels, 0.1);
  const speechLevel = percentile(sortedLevels, 0.9);
  // Clamped so a digitally-silent gap yields a large-but-finite ratio rather than Infinity.
  const noiseFloorDb = Math.max(MIN_MEASURABLE_DB, toDb(noiseFloor));
  const speechLevelDb = toDb(speechLevel);
  const signalToNoiseDb = Number.isFinite(speechLevelDb) ? speechLevelDb - noiseFloorDb : 0;

  // Did anything actually get said? A clip of pure silence or pure steady hiss has no loud end
  // standing above its quiet end, and every measurement below would be noise about noise.
  const speechThreshold = noiseFloor * Math.pow(10, SPEECH_OVER_FLOOR_DB / 20);
  const speechFrameStarts = frameStarts.filter((_, i) => frameLevels[i] > speechThreshold);
  // An audible clip needs both a real dynamic gap AND a speech level above the floor of hearing.
  const heardSpeech = speechFrameStarts.length >= 3 && signalToNoiseDb >= 3 && speechLevelDb > -60;
  if (!heardSpeech) return { ...empty, durationSeconds, sampleRate, speechLevelDb, noiseFloorDb, signalToNoiseDb };

  let clipped = 0;
  for (let i = 0; i < samples.length; i++) {
    if (Math.abs(samples[i]) >= CLIP_CEILING) clipped++;
  }
  const clippedFraction = samples.length > 0 ? clipped / samples.length : 0;

  // Bandwidth: compare the band only a wideband capture can fill against the speech band. Measured
  // on SPEECH frames only - the gaps carry no high-frequency content in any capture and would make
  // every microphone look narrowband.
  const nyquist = sampleRate / 2;
  let highBandRatioDb = -Infinity;
  let narrowband: boolean;
  if (nyquist <= HIGH_BAND_LOW_HZ) {
    // The device did not even deliver enough bandwidth to ask the question - it IS narrowband.
    narrowband = true;
  } else {
    const power = averageSpectrum(samples, speechFrameStarts);
    const speechBand = bandEnergy(power, sampleRate, SPEECH_BAND_LOW_HZ, SPEECH_BAND_HIGH_HZ);
    const highBand = bandEnergy(power, sampleRate, HIGH_BAND_LOW_HZ, Math.min(HIGH_BAND_HIGH_HZ, nyquist));
    highBandRatioDb = speechBand > 0 ? 10 * Math.log10(highBand / speechBand) : -Infinity;
    narrowband = highBandRatioDb < NARROWBAND_RATIO_DB;
  }

  return {
    heardSpeech: true,
    durationSeconds,
    sampleRate,
    speechLevelDb,
    noiseFloorDb,
    signalToNoiseDb,
    clippedFraction,
    highBandRatioDb,
    narrowband,
  };
}

/**
 * Fold the measurements into the verdict both clients render. Ordered worst-first so the first issue
 * shown is the one most worth fixing.
 */
export function judgeMicQuality(report: MicQualityReport): MicQualityVerdict {
  if (!report.heardSpeech) {
    return {
      rating: "poor",
      headline: "We did not hear any speech in that recording.",
      issues: [
        {
          id: "no-speech",
          severity: "poor",
          title: "No speech was detected.",
          advice:
            "Check that the right microphone is selected and that it is not muted, then record again " +
            "and speak a full sentence at your normal volume.",
        },
      ],
      report,
    };
  }

  const issues: MicQualityIssue[] = [];

  // Narrowband first: it is the single most destructive defect for transcription and the one users
  // never suspect, because the audio sounds "fine, just a bit muffled" to a human ear.
  if (report.narrowband) {
    issues.push({
      id: "narrowband",
      severity: "poor",
      title: "Your microphone is sending telephone-quality audio.",
      advice:
        "This is almost always a Bluetooth headset running in hands-free mode, which drops the audio " +
        "to 8 kHz and strips out the consonants. Switch the headset to its headphones profile and use " +
        "a different microphone, use the headset's dongle instead of Bluetooth, or use a wired " +
        "microphone. This alone is usually the difference between dictation working and not.",
    });
  }

  if (report.clippedFraction >= CLIP_FAIR) {
    const poor = report.clippedFraction >= CLIP_POOR;
    issues.push({
      id: "clipping",
      severity: poor ? "poor" : "fair",
      title: `Your audio is distorting (${(report.clippedFraction * 100).toFixed(1)}% of it is clipped).`,
      advice:
        "The microphone is too loud and the peaks are being cut flat. Move it further from your mouth, " +
        "or turn the input level down in your operating system's sound settings.",
    });
  }

  if (report.speechLevelDb < LEVEL_FAIR_DB) {
    const poor = report.speechLevelDb < LEVEL_POOR_DB;
    issues.push({
      id: "too-quiet",
      severity: poor ? "poor" : "fair",
      title: "Your voice is very quiet in the recording.",
      advice:
        "Move the microphone closer to your mouth, or raise the input level in your operating system's " +
        "sound settings.",
    });
  }

  if (report.signalToNoiseDb < SNR_FAIR_DB) {
    const poor = report.signalToNoiseDb < SNR_POOR_DB;
    issues.push({
      id: "noisy",
      severity: poor ? "poor" : "fair",
      title: "There is a lot of background noise behind your voice.",
      advice:
        "Your voice is only just louder than the room. Move the microphone closer to your mouth, or " +
        "record somewhere quieter - a fan, air conditioning or an open office all show up like this.",
    });
  }

  // Worst first, so the top card is the one worth acting on.
  issues.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "poor" ? -1 : 1));

  const rating: QualityRating = issues.some((i) => i.severity === "poor")
    ? "poor"
    : issues.length > 0
      ? "fair"
      : "good";

  const headline =
    rating === "good"
      ? "Your microphone sounds good. Dictation should work well."
      : rating === "fair"
        ? "Your microphone works, but it could be better."
        : "Your microphone is likely to give you poor dictation.";

  return { rating, headline, issues, report };
}

/** Convenience: measure and judge in one call. */
export function checkMicQuality(samples: Float32Array, sampleRate: number): MicQualityVerdict {
  return judgeMicQuality(analyzeMicQuality(samples, sampleRate));
}

/** Format a dBFS reading for display, guarding the silent case. */
export function formatDb(db: number): string {
  if (!Number.isFinite(db)) return "--";
  return `${db.toFixed(0)} dB`;
}
