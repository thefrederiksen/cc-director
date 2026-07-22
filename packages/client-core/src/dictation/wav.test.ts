import { describe, expect, it } from "vitest";
import { TRANSCRIBE_TRAILING_SILENCE_MS, withTrailingSilence } from "./wav";

// The trailing-silence run-out (dictation end-word fix): every transcription WAV the client sends gets a
// short pad of digital silence so the model does not clip the final word. These guard the pure pad helper
// (the full blobToWav16kMono needs a real AudioContext, which jsdom lacks).
describe("withTrailingSilence (dictation end-word run-out)", () => {
  it("appends exactly the right number of zero frames and preserves the audio", () => {
    const samples = new Float32Array([0.5, -0.5, 1]);
    const padded = withTrailingSilence(samples, 16000, 500); // 0.5s * 16000 = 8000 frames
    expect(padded.length).toBe(3 + 8000);
    expect(padded[0]).toBe(0.5);
    expect(padded[2]).toBe(1);
    expect(padded[3]).toBe(0); // pad starts silent
    expect(padded[padded.length - 1]).toBe(0);
  });

  it("returns the input unchanged for a non-positive pad", () => {
    const samples = new Float32Array([0.1, 0.2]);
    expect(withTrailingSilence(samples, 16000, 0)).toBe(samples);
    expect(withTrailingSilence(samples, 16000, -100)).toBe(samples);
  });

  it("does not mutate the input array", () => {
    const samples = new Float32Array([0.3, 0.4]);
    const before = new Float32Array(samples);
    withTrailingSilence(samples, 16000, 600);
    expect(samples).toEqual(before);
  });

  it("ships a positive default run-out", () => {
    expect(TRANSCRIBE_TRAILING_SILENCE_MS).toBeGreaterThan(0);
  });
});
