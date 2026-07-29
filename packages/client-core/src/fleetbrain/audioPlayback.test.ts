import { describe, expect, it, vi } from "vitest";
import { playClip } from "./audioPlayback";

// playClip is the single-clip playback leaf every spoken surface uses. The correctness rule it exists
// to guarantee: a clip's src is assigned EXACTLY ONCE per call, so a clip that is still playing can never be
// clobbered - the defect that made the phone play only the tail of the reply. These tests drive a fake
// media element (no jsdom needed) so the invariant is checked without a real audio engine; the real-browser
// audio-event test proves the same invariant against a real <audio> element.

/** A minimal stand-in for the parts of HTMLAudioElement playClip touches, with a COUNTER on src so a test
 *  can assert the src was assigned exactly once (never reassigned while a clip is playing). */
class FakeAudio {
  private _src = "";
  srcAssignments = 0;
  onended: (() => void) | null = null;
  onerror: (() => void) | null = null;
  playCalls = 0;
  private _playResult: Promise<void> = Promise.resolve();

  get src(): string {
    return this._src;
  }
  set src(value: string) {
    this._src = value;
    this.srcAssignments += 1;
  }

  /** Make the next play() reject, to model an autoplay block. */
  rejectPlayWith(err: Error): void {
    this._playResult = Promise.reject(err);
  }

  play(): Promise<void> {
    this.playCalls += 1;
    return this._playResult;
  }

  fireEnded(): void {
    this.onended?.();
  }
  fireError(): void {
    this.onerror?.();
  }

  asElement(): HTMLAudioElement {
    return this as unknown as HTMLAudioElement;
  }
}

describe("playClip", () => {
  it("plays the clip and resolves 'ended' when the element finishes, assigning src exactly once", async () => {
    const audio = new FakeAudio();
    const started = vi.fn();
    const ended = vi.fn();

    const p = playClip(audio.asElement(), "blob:reply", () => {}, {
      onPlayStarted: started,
      onPlayEnded: ended,
    });

    // play-started fires synchronously with the single src assignment and the single play() call.
    expect(audio.srcAssignments).toBe(1);
    expect(audio.src).toBe("blob:reply");
    expect(audio.playCalls).toBe(1);
    expect(started).toHaveBeenCalledTimes(1);
    expect(ended).not.toHaveBeenCalled();

    audio.fireEnded();
    await expect(p).resolves.toBe("ended");
    expect(ended).toHaveBeenCalledTimes(1);
    expect(ended).toHaveBeenCalledWith("ended");
    // The src was never reassigned - the whole clip played on one assignment (no clobber).
    expect(audio.srcAssignments).toBe(1);
  });

  it("resolves 'stopped' when the registered stop is invoked (an interrupt, or the surface closing)", async () => {
    const audio = new FakeAudio();
    const ended = vi.fn();
    let stop: () => void = () => {};

    const p = playClip(audio.asElement(), "blob:reply", (s) => (stop = s), { onPlayEnded: ended });
    stop();

    await expect(p).resolves.toBe("stopped");
    expect(ended).toHaveBeenCalledWith("stopped");
  });

  it("resolves 'stopped' when play() rejects (autoplay block) so the turn loop never hangs", async () => {
    const audio = new FakeAudio();
    audio.rejectPlayWith(new Error("autoplay blocked"));

    const p = playClip(audio.asElement(), "blob:reply", () => {});
    await expect(p).resolves.toBe("stopped");
  });

  it("resolves 'stopped' when the element errors (an undecodable clip)", async () => {
    const audio = new FakeAudio();
    const p = playClip(audio.asElement(), "blob:reply", () => {});
    audio.fireError();
    await expect(p).resolves.toBe("stopped");
  });

  it("resolves once: a stop AFTER the clip already ended cannot flip the outcome or double-fire the hook", async () => {
    const audio = new FakeAudio();
    const ended = vi.fn();
    let stop: () => void = () => {};

    const p = playClip(audio.asElement(), "blob:reply", (s) => (stop = s), { onPlayEnded: ended });
    audio.fireEnded();
    await expect(p).resolves.toBe("ended");

    // playClip re-registers a no-op stop once done, so a late interrupt is harmless.
    stop();
    audio.fireEnded();
    expect(ended).toHaveBeenCalledTimes(1);
    expect(ended).toHaveBeenCalledWith("ended");
  });

  it("detaches its handlers when done so the shared element is clean for the next turn", async () => {
    const audio = new FakeAudio();
    const p = playClip(audio.asElement(), "blob:reply", () => {});
    audio.fireEnded();
    await p;
    expect(audio.onended).toBeNull();
    expect(audio.onerror).toBeNull();
  });
});
