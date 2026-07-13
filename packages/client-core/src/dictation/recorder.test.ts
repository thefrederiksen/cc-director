import { afterEach, describe, expect, it, vi } from "vitest";
import { MicRecorder } from "./recorder";

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
