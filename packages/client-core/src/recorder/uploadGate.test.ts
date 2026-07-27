import { describe, expect, it } from "vitest";

// The pure upload-pass decisions (issue #958), ported from the Android RecordingUploadGate. These
// tests pin the rules that guarantee "the audio AND the notes always upload": above all, that
// "uploaded" is NOT terminal - only uploaded AND completed (the complete call acknowledged) is, so
// a regression back to treating all-segments-sent as done cannot land silently.

import { isFullyDelivered, needsUpload, requeueIndicesForResend, shouldUploadAudio } from "./uploadGate";

describe("needsUpload", () => {
  it("picks up every state where the audio is not fully on the server", () => {
    expect(needsUpload("queued", false)).toBe(true);
    expect(needsUpload("retry", false)).toBe(true);
    expect(needsUpload("uploading", false)).toBe(true);
  });

  it("still needs work when the audio is up but the complete call was never acknowledged - the notes ride only on complete", () => {
    expect(needsUpload("uploaded", false)).toBe(true);
  });

  it("is done only when uploaded AND completed", () => {
    expect(needsUpload("uploaded", true)).toBe(false);
  });

  it("picks up a legacy 'ready' row - uploading is automatic, nothing waits for a Send press (devthrottle_internal#966)", () => {
    expect(needsUpload("ready", false)).toBe(true);
  });

  it("never picks up a recording still being captured", () => {
    expect(needsUpload("recording", false)).toBe(false);
  });
});

describe("shouldUploadAudio", () => {
  it("runs the audio phase unless every segment is already confirmed", () => {
    expect(shouldUploadAudio("queued")).toBe(true);
    expect(shouldUploadAudio("retry")).toBe(true);
    expect(shouldUploadAudio("uploading")).toBe(true);
  });

  it("skips straight to the complete call when the audio is already up - no bytes are re-sent", () => {
    expect(shouldUploadAudio("uploaded")).toBe(false);
  });
});

describe("isFullyDelivered", () => {
  it("is true only for uploaded AND completed", () => {
    expect(isFullyDelivered("uploaded", true)).toBe(true);
    expect(isFullyDelivered("uploaded", false)).toBe(false);
    expect(isFullyDelivered("queued", true)).toBe(false);
    expect(isFullyDelivered("ready", false)).toBe(false);
  });
});

describe("requeueIndicesForResend", () => {
  it("returns exactly the locally-present indices the gate named, deduplicated and sorted", () => {
    expect(requeueIndicesForResend([3, 1, 3, 2], [0, 1, 2, 3])).toEqual([1, 2, 3]);
  });

  it("never invents a segment the phone does not have", () => {
    expect(requeueIndicesForResend([5, 1], [0, 1])).toEqual([1]);
  });

  it("is empty for a null/undefined gate answer", () => {
    expect(requeueIndicesForResend(null, [0, 1])).toEqual([]);
    expect(requeueIndicesForResend(undefined, [0, 1])).toEqual([]);
  });
});
