import { beforeEach, describe, expect, it } from "vitest";
import { allDictationStatuses, clearDictationStatus, publishDictationStatus } from "./status";

// The store is a module-level singleton, so each test clears whatever it added to stay independent.
describe("dictation status store", () => {
  beforeEach(() => {
    for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
  });

  it("publishes a status keyed by uploadId", () => {
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "uploading", uploaded: 1, total: 3 });
    const all = allDictationStatuses();
    expect(all).toHaveLength(1);
    expect(all[0]).toMatchObject({ sessionId: "s1", uploadId: "u1", phase: "uploading", uploaded: 1, total: 3 });
    expect(all[0].updatedAt).toBeGreaterThan(0);
  });

  it("replaces the same uploadId in place rather than duplicating it", () => {
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "uploading" });
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "transcribing" });
    const all = allDictationStatuses();
    expect(all).toHaveLength(1);
    expect(all[0].phase).toBe("transcribing");
  });

  it("keeps a failed status until it is explicitly cleared", () => {
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "failed", error: "timeout", retryable: true });
    expect(allDictationStatuses()).toHaveLength(1);
    clearDictationStatus("u1");
    expect(allDictationStatuses()).toHaveLength(0);
  });

  it("returns a stable snapshot reference between mutations (useSyncExternalStore contract)", () => {
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "uploading" });
    const first = allDictationStatuses();
    const second = allDictationStatuses();
    expect(second).toBe(first);
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "transcribing" });
    expect(allDictationStatuses()).not.toBe(first);
  });

  it("tracks several sessions independently", () => {
    publishDictationStatus({ sessionId: "s1", uploadId: "u1", phase: "transcribing" });
    publishDictationStatus({ sessionId: "s2", uploadId: "u2", phase: "failed", retryable: true });
    const all = allDictationStatuses();
    expect(all.map((s) => s.sessionId).sort()).toEqual(["s1", "s2"]);
  });
});
