import { describe, expect, it } from "vitest";

// The pure pieces of the rotating capture engine (issue #958). The MediaRecorder rotation itself
// needs a real microphone and is proven in the live walk; what is pinned here is the codec label
// contract with the server - CodecToExt maps these labels by substring, so a wrong label would make
// the server store segments under the wrong extension and content type.

import { codecLabelFor, MAX_RECORDING_MS, SEGMENT_MS } from "./segmentRecorder";

describe("codecLabelFor", () => {
  it("maps every MediaRecorder container to a label the server's CodecToExt resolves correctly", () => {
    expect(codecLabelFor("audio/webm;codecs=opus")).toBe("webm-opus");
    expect(codecLabelFor("audio/webm")).toBe("webm-opus");
    expect(codecLabelFor("audio/mp4")).toBe("aac-m4a");
    expect(codecLabelFor("audio/ogg;codecs=opus")).toBe("ogg-opus");
  });

  it("defaults to webm-opus when the container is unknown", () => {
    expect(codecLabelFor("")).toBe("webm-opus");
  });
});

describe("capture constants", () => {
  it("rotates one-minute segments (the Android recorder's interval) and caps at thirty minutes", () => {
    expect(SEGMENT_MS).toBe(60_000);
    expect(MAX_RECORDING_MS).toBe(30 * 60_000);
  });
});
