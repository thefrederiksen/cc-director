import { afterEach, describe, expect, it, vi } from "vitest";
import { MicRecorder, rmsLevel } from "./recorder";

// snapshotFlushed() is the tail-loss fix for the turn-taking paths (Car Mode "Over and out" and the
// end-phrase watch): a bare snapshot() only sees chunks MediaRecorder has already delivered, so the
// last words - up to one timeslice of audio still buffered inside the recorder - were missing from
// the clip that got transcribed. These tests drive the flush contract with a fake MediaRecorder
// (the tests run in Node, where no real recorder exists): requestData() must be asked for the tail,
// the tail must be in the returned blob, and a recorder that never delivers (or has already gone
// inactive) must resolve with what has arrived instead of hanging the turn.

type DataListener = (e: { data: Blob }) => void;

class FakeMediaRecorder {
  state: "recording" | "inactive" | "paused" = "recording";
  requestDataCalls = 0;
  /** What requestData does; the test sets this to emulate the browser delivering the tail. */
  onRequestData: (() => void) | null = null;
  private onceListeners: DataListener[] = [];

  addEventListener(type: string, listener: DataListener, options?: { once?: boolean }): void {
    if (type !== "dataavailable") throw new Error(`unexpected listener type: ${type}`);
    if (!options?.once) throw new Error("snapshotFlushed must register a once-listener");
    this.onceListeners.push(listener);
  }

  requestData(): void {
    this.requestDataCalls += 1;
    this.onRequestData?.();
  }

  /** Emulate the browser firing dataavailable to the registered once-listeners. */
  deliver(data: Blob): void {
    const listeners = this.onceListeners;
    this.onceListeners = [];
    for (const l of listeners) l({ data });
  }
}

// Reach into the private capture state the way start() would have set it up. TypeScript private is
// compile-time only; the fields are the recorder's real ones, so the method under test runs unchanged.
function wire(fake: FakeMediaRecorder, chunks: Blob[]): MicRecorder {
  const mic = new MicRecorder();
  const anyMic = mic as unknown as { recorder: FakeMediaRecorder; mimeType: string; chunks: Blob[] };
  anyMic.recorder = fake;
  anyMic.mimeType = "audio/webm";
  anyMic.chunks = chunks;
  return mic;
}

function bytes(n: number): Blob {
  return new Blob([new Uint8Array(n)]);
}

afterEach(() => {
  vi.useRealTimers();
});

describe("MicRecorder.snapshotFlushed", () => {
  it("includes the tail chunk MediaRecorder had not delivered yet", async () => {
    const fake = new FakeMediaRecorder();
    const chunks: Blob[] = [bytes(3)]; // header chunk already delivered
    const mic = wire(fake, chunks);

    // The plain snapshot documents the gap: only the delivered 3 bytes.
    expect(mic.snapshot().size).toBe(3);

    // requestData makes the browser flush the buffered tail: the production ondataavailable handler
    // (registered first) pushes it, then the once-listener resolves - emulate exactly that order.
    fake.onRequestData = () => {
      chunks.push(bytes(5));
      fake.deliver(bytes(5));
    };

    const flushed = await mic.snapshotFlushed();
    expect(fake.requestDataCalls).toBe(1);
    expect(flushed.size).toBe(8); // 3 delivered + 5 flushed tail
  });

  it("returns the plain snapshot when the recorder is not actively recording", async () => {
    const fake = new FakeMediaRecorder();
    fake.state = "inactive";
    const mic = wire(fake, [bytes(4)]);

    const flushed = await mic.snapshotFlushed();
    expect(fake.requestDataCalls).toBe(0); // nothing is buffered in an inactive recorder
    expect(flushed.size).toBe(4);
  });

  it("resolves via the backstop when the flush is never delivered, with what has arrived", async () => {
    vi.useFakeTimers();
    const fake = new FakeMediaRecorder();
    const mic = wire(fake, [bytes(7)]);
    // requestData is called but the recorder never fires dataavailable (a wedged recorder).

    const pending = mic.snapshotFlushed();
    await vi.advanceTimersByTimeAsync(500);
    const flushed = await pending;
    expect(fake.requestDataCalls).toBe(1);
    expect(flushed.size).toBe(7);
  });

  it("resolves with what has arrived when requestData throws (a concurrent stop)", async () => {
    const fake = new FakeMediaRecorder();
    fake.onRequestData = () => {
      throw new Error("The MediaRecorder is in an invalid state.");
    };
    const mic = wire(fake, [bytes(2)]);

    const flushed = await mic.snapshotFlushed();
    expect(flushed.size).toBe(2);
  });

  it("does not resolve twice when the delivery and the backstop both fire", async () => {
    vi.useFakeTimers();
    const fake = new FakeMediaRecorder();
    const chunks: Blob[] = [bytes(1)];
    const mic = wire(fake, chunks);
    fake.onRequestData = () => {
      chunks.push(bytes(1));
      fake.deliver(bytes(1));
    };

    const flushed = await mic.snapshotFlushed();
    expect(flushed.size).toBe(2);
    // The backstop timer was cleared on delivery; advancing time must not blow up on a settled promise.
    await vi.advanceTimersByTimeAsync(1000);
  });
});

// The equalizer meter (issue: the Cockpit Speak bars were sluggish/near-flat). rmsLevel reads the live
// WAVEFORM (getByteTimeDomainData: bytes centred on 128, silence = 128) as instantaneous loudness, so the
// bars track the speaker in real time. These pin the shape the equalizer relies on.
describe("rmsLevel", () => {
  // A window of pure silence is every sample sitting at the 128 centre.
  function silence(n: number): Uint8Array {
    return new Uint8Array(n).fill(128);
  }
  // A full-scale square wave: samples alternate between the two rails (0 and 255), the loudest possible
  // read.
  function fullScale(n: number): Uint8Array {
    const buf = new Uint8Array(n);
    for (let i = 0; i < n; i++) buf[i] = i % 2 === 0 ? 0 : 255;
    return buf;
  }

  it("reads silence (all samples at the centre) as zero", () => {
    expect(rmsLevel(silence(512))).toBe(0);
  });

  it("reads a full-scale waveform as the clamped maximum of 1", () => {
    expect(rmsLevel(fullScale(512))).toBe(1);
  });

  it("reads an empty window as zero rather than dividing by zero", () => {
    expect(rmsLevel(new Uint8Array(0))).toBe(0);
  });

  it("rises monotonically as the waveform gets louder", () => {
    const quiet = new Uint8Array(512);
    const loud = new Uint8Array(512);
    for (let i = 0; i < 512; i++) {
      // Small deviation from centre vs a larger one - louder audio swings further from 128.
      quiet[i] = i % 2 === 0 ? 118 : 138; // +-10
      loud[i] = i % 2 === 0 ? 88 : 168; // +-40
    }
    const q = rmsLevel(quiet);
    const l = rmsLevel(loud);
    expect(q).toBeGreaterThan(0);
    expect(l).toBeGreaterThan(q);
  });

  it("stays within 0..1 for any input", () => {
    const random = new Uint8Array(512);
    for (let i = 0; i < 512; i++) random[i] = (i * 37) % 256;
    const v = rmsLevel(random);
    expect(v).toBeGreaterThanOrEqual(0);
    expect(v).toBeLessThanOrEqual(1);
  });
});

// The suspended-context guard (the Cockpit "bars never bounce" defect): the meter's AudioContext is
// created after the async getUserMedia round trip, so the browser's autoplay policy can start it
// SUSPENDED - and a suspended analyser reads the flat centre line forever, pinning the equalizer at
// zero while MediaRecorder captures fine. level() must re-ask a suspended context to resume on every
// read (it runs once per animation frame), so the meter self-heals instead of staying dead for the
// whole dictation. These drive the REAL level() with the private capture fields wired in, the same
// way the snapshotFlushed tests wire theirs - not a copy of the logic.
describe("MicRecorder.level suspended-context guard", () => {
  // The analyser hands back a clearly loud waveform so a healed meter is distinguishable from silence.
  class FakeAnalyser {
    getByteTimeDomainData(buf: Uint8Array): void {
      for (let i = 0; i < buf.length; i++) buf[i] = i % 2 === 0 ? 88 : 168;
    }
  }

  class FakeAudioContext {
    state: "suspended" | "running";
    resumeCalls = 0;
    constructor(state: "suspended" | "running") {
      this.state = state;
    }
    resume(): Promise<void> {
      this.resumeCalls += 1;
      this.state = "running";
      return Promise.resolve();
    }
  }

  function wireLevel(ctx: FakeAudioContext): MicRecorder {
    const mic = new MicRecorder();
    const anyMic = mic as unknown as { audioCtx: FakeAudioContext; analyser: FakeAnalyser; levelData: Uint8Array };
    anyMic.audioCtx = ctx;
    anyMic.analyser = new FakeAnalyser();
    anyMic.levelData = new Uint8Array(8);
    return mic;
  }

  it("asks a suspended context to resume, once per read, until it runs", () => {
    const ctx = new FakeAudioContext("suspended");
    const mic = wireLevel(ctx);
    mic.level();
    expect(ctx.resumeCalls).toBe(1);
    // The next frame's read sees the context running and leaves it alone.
    mic.level();
    expect(ctx.resumeCalls).toBe(1);
  });

  it("never touches resume on a context that is already running", () => {
    const ctx = new FakeAudioContext("running");
    const mic = wireLevel(ctx);
    expect(mic.level()).toBeGreaterThan(0);
    expect(ctx.resumeCalls).toBe(0);
  });

  it("still returns the analyser's reading on the same call that resumes", () => {
    // The resume is fire-and-forget; the read itself must not be skipped or zeroed by the guard.
    const ctx = new FakeAudioContext("suspended");
    const mic = wireLevel(ctx);
    expect(mic.level()).toBeGreaterThan(0);
  });
});
