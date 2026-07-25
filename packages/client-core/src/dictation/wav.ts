// Transcode a captured audio Blob (Opus-in-WebM or whatever MediaRecorder produced) into a
// 16 kHz mono 16-bit PCM WAV Blob for the transcription upload (issue #817).
//
// Why WAV: the Gateway's batch transcription pipeline accepts a clip in every mode, but in the
// default Local (in-process Whisper.net) mode it feeds the bytes straight into a WAV reader - it
// does not decode Opus/WebM. Encoding WAV here makes dictation work in Local mode AND the remote
// (byo / devthrottle / OpenAI) modes from one client path, instead of only working when a remote
// key happens to be configured. The browser decodes its own recording with decodeAudioData and an
// OfflineAudioContext resamples + downmixes to 16 kHz mono.

const TARGET_SAMPLE_RATE = 16000;

// Trailing silence appended to every transcription WAV (the dictation end-word fix). Capture on every
// surface keeps the final audio bytes (all end paths use a race-free stop), yet the last WORD still goes
// missing: the batch transcription model (Whisper) drops or clips the final token when a clip ends
// abruptly on speech with no run-out - exactly what happens when the user hits Send/Pause the instant he
// stops talking. A short pad of digital silence gives the model the trailing anchor it needs so the last
// word survives. This is model run-out room, NOT captured audio: it lengthens the WAV sent for
// transcription but is never counted as captured audio (decodedSeconds stays the real decoded duration).
export const TRANSCRIBE_TRAILING_SILENCE_MS = 600;

/** Append `ms` of digital silence (zero samples) after `samples` at `sampleRate`, returning a NEW array
 *  (the input is not mutated). A non-positive `ms` returns the samples unchanged. Shared so every WAV the
 *  client sends for transcription carries the same run-out room. */
export function withTrailingSilence(samples: Float32Array, sampleRate: number, ms: number): Float32Array {
  if (ms <= 0) return samples;
  const padFrames = Math.round((ms / 1000) * sampleRate);
  if (padFrames <= 0) return samples;
  const padded = new Float32Array(samples.length + padFrames); // new arrays are zero-filled = silence
  padded.set(samples, 0);
  return padded;
}

/** The WAV plus the capture-health facts the decode already revealed (issue #863): the decoded
 *  audio duration (the actual amount of sound captured) and the source blob size. The caller
 *  compares decodedSeconds to the recording wall-clock to detect dropped audio. */
export interface TranscodeResult {
  wav: Blob;
  decodedSeconds: number;
  sourceBytes: number;
  /** The clip at the microphone's NATIVE rate, mono - the form the microphone-quality measurement
   *  needs (micQuality.ts), which the 16 kHz WAV above cannot answer because resampling discards
   *  everything above 8 kHz and with it the evidence that tells a wideband microphone from a
   *  telephone-grade link. Handed back from the decode this function already performs so background
   *  quality reporting costs no second decode of a multi-megabyte clip on the dictation path. */
  nativeSamples: Float32Array;
  nativeSampleRate: number;
}

export async function blobToWav16kMono(blob: Blob): Promise<TranscodeResult> {
  const arrayBuffer = await blob.arrayBuffer();
  if (arrayBuffer.byteLength === 0) throw new Error("No audio was captured.");
  const sourceBytes = arrayBuffer.byteLength;

  const AudioCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
  const decodeCtx = new AudioCtor();
  let decoded: AudioBuffer;
  try {
    decoded = await decodeCtx.decodeAudioData(arrayBuffer.slice(0));
  } finally {
    void decodeCtx.close();
  }

  // Resample to 16 kHz and downmix to mono. An OfflineAudioContext destination with 1 channel
  // mixes a stereo source down for us when the source is connected to it.
  const frameCount = Math.max(1, Math.ceil(decoded.duration * TARGET_SAMPLE_RATE));
  const offline = new OfflineAudioContext(1, frameCount, TARGET_SAMPLE_RATE);
  const source = offline.createBufferSource();
  source.buffer = decoded;
  source.connect(offline.destination);
  source.start(0);
  const rendered = await offline.startRendering();

  // Pad the model's run-out room onto the WAV, but report the REAL decoded duration for capture-health:
  // the pad is silence for the transcriber, never audio we claim to have captured.
  const padded = withTrailingSilence(rendered.getChannelData(0), TARGET_SAMPLE_RATE, TRANSCRIBE_TRAILING_SILENCE_MS);
  return {
    wav: encodeWav(padded, TARGET_SAMPLE_RATE),
    decodedSeconds: decoded.duration,
    sourceBytes,
    nativeSamples: monoFrom(decoded),
    nativeSampleRate: decoded.sampleRate,
  };
}

/** A captured clip decoded at the rate the MICROPHONE actually delivered, downmixed to mono. */
export interface DecodedClip {
  samples: Float32Array;
  sampleRate: number;
}

/**
 * Decode a captured blob to mono at its NATIVE sample rate, for the microphone check (micQuality.ts).
 *
 * Deliberately separate from blobToWav16kMono: that function resamples to 16 kHz for transcription,
 * which throws away everything above 8 kHz - and the presence or absence of energy up there is
 * exactly the evidence that distinguishes a wideband microphone from a Bluetooth hands-free link.
 * Measuring the resampled clip would make every microphone look identical. This decodes once at the
 * native rate and changes nothing on the live dictation path.
 */
export async function decodeToMono(blob: Blob): Promise<DecodedClip> {
  const arrayBuffer = await blob.arrayBuffer();
  if (arrayBuffer.byteLength === 0) throw new Error("No audio was captured.");

  const AudioCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
  const decodeCtx = new AudioCtor();
  let decoded: AudioBuffer;
  try {
    decoded = await decodeCtx.decodeAudioData(arrayBuffer.slice(0));
  } finally {
    void decodeCtx.close();
  }

  return { samples: monoFrom(decoded), sampleRate: decoded.sampleRate };
}

/**
 * One mono channel from a decoded buffer. A mono source is returned as-is (a view over memory that
 * already exists, so this costs nothing on the dictation path, where capture requests one channel).
 * Several channels are AVERAGED rather than picking the first: a headset that feeds only one side
 * would otherwise measure as silence whenever we happened to choose the empty channel.
 */
function monoFrom(decoded: AudioBuffer): Float32Array {
  if (decoded.numberOfChannels === 1) return decoded.getChannelData(0);
  const frames = decoded.length;
  const mixed = new Float32Array(frames);
  for (let ch = 0; ch < decoded.numberOfChannels; ch++) {
    const data = decoded.getChannelData(ch);
    for (let i = 0; i < frames; i++) mixed[i] += data[i];
  }
  for (let i = 0; i < frames; i++) mixed[i] /= decoded.numberOfChannels;
  return mixed;
}

// Build a canonical 16-bit PCM WAV (RIFF) blob from mono float samples in -1..1.
function encodeWav(samples: Float32Array, sampleRate: number): Blob {
  const bytesPerSample = 2;
  const dataLength = samples.length * bytesPerSample;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);

  writeAscii(view, 0, "RIFF");
  view.setUint32(4, 36 + dataLength, true);
  writeAscii(view, 8, "WAVE");
  writeAscii(view, 12, "fmt ");
  view.setUint32(16, 16, true); // PCM fmt chunk size
  view.setUint16(20, 1, true); // PCM format
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * bytesPerSample, true); // byte rate
  view.setUint16(32, bytesPerSample, true); // block align
  view.setUint16(34, 16, true); // bits per sample
  writeAscii(view, 36, "data");
  view.setUint32(40, dataLength, true);

  let offset = 44;
  for (let i = 0; i < samples.length; i++) {
    const clamped = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
    offset += bytesPerSample;
  }

  return new Blob([buffer], { type: "audio/wav" });
}

function writeAscii(view: DataView, offset: number, text: string): void {
  for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
}
