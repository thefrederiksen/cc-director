import { afterEach, describe, expect, it } from "vitest";
import { playReadyCue } from "./readyCue";

// The ready cue is best-effort audio: it must play the right shape when Web Audio is available and
// must never throw when it is not. These tests inject a fake AudioContext (the tests run in Node,
// where window/AudioContext do not exist) to verify the bloop wiring without a real sound device.

interface Ramp {
  method: "setValueAtTime" | "exponentialRampToValueAtTime";
  value: number;
  time: number;
}

class FakeParam {
  ramps: Ramp[] = [];
  setValueAtTime(value: number, time: number) {
    this.ramps.push({ method: "setValueAtTime", value, time });
  }
  exponentialRampToValueAtTime(value: number, time: number) {
    this.ramps.push({ method: "exponentialRampToValueAtTime", value, time });
  }
}

class FakeNode {
  connect(target: FakeNode) {
    return target;
  }
}

class FakeOscillator extends FakeNode {
  type = "";
  frequency = new FakeParam();
  started = false;
  stopped = false;
  onended: (() => void) | null = null;
  start() {
    this.started = true;
  }
  stop() {
    this.stopped = true;
  }
}

class FakeGain extends FakeNode {
  gain = new FakeParam();
}

class FakeAudioContext {
  static last: FakeAudioContext | null = null;
  state = "running";
  currentTime = 0;
  destination = new FakeNode();
  osc = new FakeOscillator();
  gainNode = new FakeGain();
  closed = false;
  resumed = false;
  constructor() {
    FakeAudioContext.last = this;
  }
  createOscillator() {
    return this.osc;
  }
  createGain() {
    return this.gainNode;
  }
  resume() {
    this.resumed = true;
    return Promise.resolve();
  }
  close() {
    this.closed = true;
    return Promise.resolve();
  }
}

const g = globalThis as unknown as { window?: unknown };

afterEach(() => {
  delete g.window;
  FakeAudioContext.last = null;
});

describe("playReadyCue", () => {
  it("synthesizes a rising-pitch decaying bloop through Web Audio", () => {
    g.window = { AudioContext: FakeAudioContext };

    playReadyCue();

    const ctx = FakeAudioContext.last;
    expect(ctx).not.toBeNull();
    const osc = ctx!.osc;
    expect(osc.type).toBe("sine");
    expect(osc.started).toBe(true);
    expect(osc.stopped).toBe(true);

    // Pitch glides UP (the droplet "plink"): start low, ramp to a higher target.
    const start = osc.frequency.ramps.find((r) => r.method === "setValueAtTime");
    const rampUp = osc.frequency.ramps.find((r) => r.method === "exponentialRampToValueAtTime");
    expect(start).toBeDefined();
    expect(rampUp).toBeDefined();
    expect(rampUp!.value).toBeGreaterThan(start!.value);

    // Gain: fast attack then a decay back down to a near-silent floor.
    const gainRamps = ctx!.gainNode.gain.ramps;
    const peak = Math.max(...gainRamps.map((r) => r.value));
    const last = gainRamps[gainRamps.length - 1];
    expect(peak).toBeGreaterThan(0.1);
    expect(last.value).toBeLessThan(peak);
  });

  it("resumes a suspended context so the cue is audible", () => {
    class SuspendedCtx extends FakeAudioContext {
      constructor() {
        super();
        this.state = "suspended";
      }
    }
    g.window = { AudioContext: SuspendedCtx };

    playReadyCue();

    expect((FakeAudioContext.last as SuspendedCtx).resumed).toBe(true);
  });

  it("does nothing and never throws when Web Audio is unavailable", () => {
    g.window = {}; // no AudioContext
    expect(() => playReadyCue()).not.toThrow();
    expect(FakeAudioContext.last).toBeNull();
  });
});
