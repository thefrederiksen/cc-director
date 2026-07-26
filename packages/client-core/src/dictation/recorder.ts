// Microphone capture for the mobile dictation dialog (issue #817). Whole-clip BATCH: the mic
// records a segment, and on stop the captured audio is handed back as one Blob to be transcoded
// and transcribed. No text appears while talking (the canonical contract,
// docs/architecture/dictation/DICTATION_UX_SPEC.md).
//
// The recorder also exposes a live input level (0..1) sampled from an AnalyserNode on the live
// stream, which the dialog draws as the equalizer. This is display-only; it never touches the
// captured audio.

import { resolveMicrophoneIdentity } from "./deviceIdentity";

// Pick a MediaRecorder container the browser actually supports, preferring Opus-in-WebM (what
// every Chromium/Firefox phone produces). The captured blob is transcoded to WAV before upload,
// so the exact container here only needs to be decodable by the browser's own decodeAudioData.
function pickMimeType(): string {
  const candidates = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"];
  for (const c of candidates) {
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(c)) return c;
  }
  return "";
}

// How often MediaRecorder flushes a chunk while recording. A timeslice makes the recorder deliver
// encoded audio DURING capture (not only on stop), which gives us a genuine "first real audio
// arrived" event - the web twin of the desktop recorder's first captured PCM buffer. It does not
// change the captured clip: the chunks are concatenated in order on stop exactly as before.
const CHUNK_MS = 100;

// Backstop for snapshotFlushed(): how long to wait for MediaRecorder to deliver the flushed tail
// before snapshotting whatever has arrived. The browser twin of the desktop recorder's 750ms
// RecordingStopped drain backstop - a wedged recorder must not hang the turn, and the timeout is
// logged so a real occurrence is visible.
const FLUSH_BACKSTOP_MS = 500;

// The equalizer time window. getByteTimeDomainData fills this many samples of the live waveform; at a
// typical 48 kHz that is ~11 ms, a long enough window for a steady loudness reading yet short enough to
// track speech syllables so the bars actually bob rather than crawl.
const LEVEL_FFT_SIZE = 512;

// Turn a window of live waveform samples (getByteTimeDomainData: bytes centred on 128, silence = 128)
// into a 0..1 loudness for the equalizer. Root-mean-square of the samples' deviation from the centre is
// the instantaneous loudness - it responds immediately to how loud the speaker is right now, unlike the
// old frequency-bin average (which diluted voice energy across mostly-empty high bins) and needs no
// analyser smoothing (which only lagged the meter). A modest gain lets normal speech fill the bars while
// the clamp keeps a shout at full scale. Pure and display-only: it never touches the captured audio.
export function rmsLevel(timeDomain: Uint8Array): number {
  if (timeDomain.length === 0) return 0;
  let sumSquares = 0;
  for (let i = 0; i < timeDomain.length; i++) {
    const deviation = (timeDomain[i] - 128) / 128; // -1..1, silence -> 0
    sumSquares += deviation * deviation;
  }
  const rms = Math.sqrt(sumSquares / timeDomain.length); // 0..1
  return Math.min(1, rms * 3.2);
}

// How long to wait for the microphone to open before giving up on it. getUserMedia is specified to
// resolve or reject, but in practice it can do NEITHER - a contended or wedged capture device leaves
// the promise pending forever. Without a backstop the caller sits in its "starting" state with no
// error and no recording, which reads to the user as a dead button. The dictation dialog has carried
// its own version of this backstop since issue #817; this is the same guard for the voice checks.
export const MIC_OPEN_TIMEOUT_MS = 8000;

/**
 * Open the microphone, but fail LOUDLY if it does not open within the timeout instead of hanging.
 * The recorder is disposed on timeout so a stream that arrives late cannot leave the microphone live
 * behind a screen that has already given up on it.
 */
export async function startRecorderWithTimeout(recorder: MicRecorder, timeoutMs = MIC_OPEN_TIMEOUT_MS): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(
      () =>
        reject(
          new Error(
            `The microphone did not open within ${Math.round(timeoutMs / 1000)} seconds. It may be in use by ` +
              "another application, or disconnected.",
          ),
        ),
      timeoutMs,
    );
  });

  try {
    await Promise.race([recorder.start(), timeout]);
  } catch (err) {
    recorder.dispose();
    throw err;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

export class MicRecorder {
  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private mimeType = "";
  private audioCtx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private levelData: Uint8Array | null = null;

  // One-shot latch + callback for the honest "the microphone is now capturing your voice" moment:
  // fired when the FIRST real audio chunk lands, not merely when start() returned. The dialog uses
  // it to flip to RECORDING and play the ready cue only once audio is actually flowing.
  private captureLiveFired = false;
  onCaptureLive: (() => void) | null = null;

  // Capture-health (issue #863): wall-clock of the segment the mic was actually open.
  // Compared against the DECODED audio duration of the captured blob (in wav.ts) to detect
  // dropped audio - the browser analog of the desktop expected-vs-captured byte check. A
  // compressed MediaRecorder blob has no fixed bytes/sec, so duration, not bytes, is the yardstick.
  // Anchored at the FIRST real audio chunk (not at start()), so it excludes the mic warm-up gap and
  // lines up with both the displayed timer and the decoded audio - otherwise the warm-up would read as
  // phantom dropped audio. 0 until the first chunk arrives.
  private startedAt = 0;
  private recordedMs = 0;

  // The microphone's name and stable id, read at start() and deliberately NOT cleared when the
  // stream is released - the quality report is assembled after stop(), by which time the track is
  // gone. The label starts as the raw track label and is UPGRADED in the background by
  // resolveMicrophoneIdentity() once enumerateDevices() answers (issue #2183: a track captured via
  // the default slot is labelled literally "Default", which cannot discriminate between devices).
  private capturedDeviceLabel = "";
  private capturedDeviceId = "";

  /** True while a segment is actively capturing. */
  get isRecording(): boolean {
    return this.recorder !== null && this.recorder.state === "recording";
  }

  /**
   * The name of the microphone this segment was captured with, as the operating system reports it
   * ("Headset (Jabra Evolve2 65)", "Microphone Array (Realtek)"). Empty when the browser withholds
   * it, which it does until the user has granted microphone permission at least once.
   *
   * This is what makes quality reporting actionable rather than merely true. "Your audio is
   * band-limited" tells someone almost nothing; "your Jabra headset is band-limited and your laptop
   * microphone is not" tells them which one to stop using. Captured at start() and kept after the
   * stream is released, so the label survives to be reported alongside the finished measurement.
   */
  get deviceLabel(): string {
    return this.capturedDeviceLabel;
  }

  /**
   * The stable identifier of the microphone this segment was captured with, from the live track's
   * getSettings().deviceId resolved through enumerateDevices(). Empty when the browser withholds
   * it. This - not the label - is what quality measurements are GROUPED by on the Gateway: a driver
   * update or an operating system language change renames the label, and a grouping keyed on the
   * name would silently split one microphone into two histories.
   */
  get deviceId(): string {
    return this.capturedDeviceId;
  }

  /** Wall-clock milliseconds the most recently stopped segment was capturing. 0 before the first stop. */
  get lastRecordedMs(): number {
    return this.recordedMs;
  }

  /**
   * Open the microphone and start capturing a fresh segment. Throws if permission is denied or
   * no audio device is available - the caller surfaces the reason (no silent fallback).
   */
  async start(): Promise<void> {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error("This browser does not support microphone capture (getUserMedia).");
    }
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 },
    });

    // Read the device name while the track is live. A browser reports an empty label until the user
    // has granted permission at least once, so this is best-effort by design - an unnamed microphone
    // still reports its measurements, it just cannot be told apart from another unnamed one.
    const track = this.stream.getAudioTracks()[0];
    this.capturedDeviceLabel = track?.label ?? "";
    this.capturedDeviceId = "";
    // Resolve the REAL device behind the label in the background (issue #2183: a default-slot
    // capture is labelled literally "Default"). Fire-and-forget by the standing contract - this
    // must never delay a word of the user's dictation. It settles in milliseconds while the
    // shortest reportable clip is three seconds, so the quality report reads the resolved identity.
    void this.resolveDeviceIdentity(track);

    // Live level meter on the captured stream (display only).
    const AudioCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    this.audioCtx = new AudioCtor();
    const source = this.audioCtx.createMediaStreamSource(this.stream);
    this.analyser = this.audioCtx.createAnalyser();
    // Sized for a live-waveform (time-domain) read: the buffer holds fftSize samples of the raw
    // waveform, which rmsLevel() turns into an instantaneous loudness. No smoothingTimeConstant is set
    // because that only shapes frequency-domain reads (which we no longer use) and only ever lagged the
    // meter.
    this.analyser.fftSize = LEVEL_FFT_SIZE;
    source.connect(this.analyser);
    this.levelData = new Uint8Array(this.analyser.fftSize);

    this.mimeType = pickMimeType();
    this.recorder = this.mimeType
      ? new MediaRecorder(this.stream, { mimeType: this.mimeType })
      : new MediaRecorder(this.stream);
    this.chunks = [];
    this.captureLiveFired = false;
    // Reset the capture-health wall-clock; it is anchored at the FIRST real audio below, not here, so a
    // segment that never delivers a chunk reports recordedMs = 0 rather than a stale previous value.
    this.startedAt = 0;
    this.recorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) {
        this.chunks.push(e.data);
        // The first real chunk is the honest "mic is capturing your voice" moment. Fire once.
        if (!this.captureLiveFired) {
          this.captureLiveFired = true;
          // Anchor the capture-health wall-clock at first audio - the same instant the displayed timer and
          // the decoded audio begin. Anchoring at start() instead would fold in the mic warm-up gap (which
          // produced no audio), inflating recordedMs into a phantom deficit and firing a false
          // dropped-audio warning on short clips.
          this.startedAt = performance.now();
          this.onCaptureLive?.();
        }
      }
    };
    // Start WITH a timeslice so chunks (and the first-audio signal) arrive during capture.
    this.recorder.start(CHUNK_MS);
  }

  /**
   * Snapshot the audio captured SO FAR as one Blob, WITHOUT stopping the recorder - the microphone keeps
   * capturing and more audio keeps accumulating. Used by Car Mode to transcribe the accumulated utterance
   * on each pause while still listening (the single-mic-stream design). The blob starts at the first chunk
   * (which carries the container header), so it is decodable; a segment that has produced no chunks yet
   * returns an empty blob. Reads the chunk list synchronously, so it is safe to call from a timer while
   * MediaRecorder is still delivering chunks on the same thread.
   */
  snapshot(): Blob {
    const mime = this.mimeType || "audio/webm";
    return new Blob(this.chunks, { type: mime });
  }

  /**
   * Snapshot ALL audio captured up to now - INCLUDING the tail still buffered inside MediaRecorder -
   * without stopping the recorder; the microphone keeps capturing and chunks keep accumulating.
   *
   * A plain snapshot() only sees chunks the recorder has already delivered, so up to CHUNK_MS of the
   * most recent speech (exactly where the final word or the sign-off phrase lands) is missing from it.
   * This variant calls requestData(), which makes MediaRecorder emit its buffered audio as an immediate
   * dataavailable, and waits for a delivery before assembling the blob - so the last words are in.
   *
   * RESIDUAL CAVEAT: the wait resolves on the FIRST dataavailable after the call, which under load can
   * be an earlier timeslice chunk that was already queued - the requestData flush then lands just after
   * the snapshot was assembled. So this is a large improvement over snapshot(), not an absolute
   * guarantee. It is the right tool ONLY for the rolling end-phrase watch, where a short miss is
   * self-correcting: the watch re-ticks every second, and a clip it commits provably contains the
   * spoken sign-off phrase (that is how the phrase was detected). A path that ENDS the turn must not
   * rely on this - it stops the recorder instead (stop() resolves only after the final chunk was
   * delivered, which is race-free). When the recorder is not actively recording there is nothing
   * buffered to flush and the plain snapshot is returned as-is.
   */
  async snapshotFlushed(): Promise<Blob> {
    const rec = this.recorder;
    if (rec === null || rec.state !== "recording") return this.snapshot();
    await new Promise<void>((resolve) => {
      let backstop: ReturnType<typeof setTimeout> | undefined;
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        if (backstop !== undefined) clearTimeout(backstop);
        resolve();
      };
      // The ondataavailable handler assigned in start() was registered first, so it has already
      // pushed the flushed chunk into this.chunks by the time this once-listener runs (event
      // listeners fire in registration order).
      rec.addEventListener("dataavailable", finish, { once: true });
      backstop = setTimeout(() => {
        console.warn(`[MicRecorder] snapshotFlushed: no flush within ${FLUSH_BACKSTOP_MS}ms; snapshotting what has arrived`);
        finish();
      }, FLUSH_BACKSTOP_MS);
      try {
        rec.requestData();
      } catch (err) {
        // The recorder went inactive between the state check and here (a concurrent stop). Nothing
        // is buffered any more; the chunks list already holds everything that was delivered.
        console.warn(`[MicRecorder] snapshotFlushed: requestData failed: ${err instanceof Error ? err.message : String(err)}`);
        finish();
      }
    });
    return this.snapshot();
  }

  /** Current input level in 0..1, sampled live from the waveform. Returns 0 when not recording. */
  level(): number {
    if (!this.analyser || !this.levelData) return 0;
    this.analyser.getByteTimeDomainData(this.levelData);
    return rmsLevel(this.levelData);
  }

  /**
   * Stop the current segment and return the captured audio as one Blob. The microphone is
   * released here, so the next segment calls start() again (a fresh Resume segment).
   */
  async stop(): Promise<Blob> {
    const rec = this.recorder;
    if (rec === null) throw new Error("Recorder was not started.");
    const mime = this.mimeType || "audio/webm";
    const captured = await new Promise<Blob>((resolve) => {
      rec.onstop = () => resolve(new Blob(this.chunks, { type: mime }));
      rec.stop();
    });
    // Freeze the segment wall-clock at stop, before releasing the stream, so capture-health can
    // compare it to the decoded audio duration of the captured blob.
    this.recordedMs = this.startedAt > 0 ? performance.now() - this.startedAt : 0;
    this.releaseStream();
    return captured;
  }

  /**
   * Resolve which real microphone is behind the live track, in the background. Never throws to the
   * caller and never delays capture: a dictation that cannot name its device still delivers every
   * word, it just reports under the raw track label like it always did.
   */
  private async resolveDeviceIdentity(track: MediaStreamTrack | undefined): Promise<void> {
    try {
      if (track === undefined || !navigator.mediaDevices?.enumerateDevices) return;
      const settingsId = typeof track.getSettings === "function" ? (track.getSettings().deviceId ?? "") : "";
      // The raw id is kept immediately so even an interrupted resolution groups consistently.
      this.capturedDeviceId = settingsId;
      const devices = await navigator.mediaDevices.enumerateDevices();
      const identity = resolveMicrophoneIdentity(track.label ?? "", settingsId, devices);
      if (identity.label !== "") this.capturedDeviceLabel = identity.label;
      if (identity.deviceId !== "") this.capturedDeviceId = identity.deviceId;
    } catch (err) {
      console.warn(`[MicRecorder] device identity resolution failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  /** Release the microphone and audio graph without producing a clip (Cancel / teardown). */
  dispose(): void {
    try {
      if (this.recorder !== null && this.recorder.state !== "inactive") this.recorder.stop();
    } catch {
      // already stopped; releasing the stream below is what matters
    }
    this.releaseStream();
  }

  private releaseStream(): void {
    if (this.stream !== null) {
      for (const track of this.stream.getTracks()) track.stop();
    }
    if (this.audioCtx !== null) {
      void this.audioCtx.close();
    }
    this.stream = null;
    this.recorder = null;
    this.audioCtx = null;
    this.analyser = null;
    this.levelData = null;
  }
}
