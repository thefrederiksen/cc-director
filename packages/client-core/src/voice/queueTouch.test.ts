// The voice queue's ordering rule (voice-mode queue flow, 2026-07-24): first-in-first-out by when
// each session last ENTERED the line - the Gateway's needsYouSince, or the device-local
// listened-touch when that is later. A listened-but-unhandled session must drop to the BOTTOM and
// come around again; sessions never heard keep their Gateway wait order.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionDto } from "../api/client";
import { getAutoSpeak, inVoiceQueueOrder, queueTouchMs, setAutoSpeak, touchQueue } from "./queueTouch";

function session(id: string, needsYouSince: string | null, createdAt = "2026-07-24T00:00:00Z"): SessionDto {
  return { sessionId: id, needsYouSince, createdAt } as unknown as SessionDto;
}

// Each test uses its own session ids so the module-level in-memory touch mirror (deliberately
// page-lifetime state in production) cannot bleed between tests.
let n = 0;
function freshId(tag: string): string {
  n += 1;
  return `${tag}-${n}`;
}

beforeEach(() => {
  // node is vitest's default environment here (there is no vitest config), so localStorage does not
  // exist and every touch would silently live only in the in-memory mirror - the durable half of the
  // store would go untested. Stub a real one, the same way rowVoiceInputs.test.ts does.
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  });
});

describe("inVoiceQueueOrder", () => {
  it("orders untouched sessions oldest-waiting first (the Gateway wait order)", () => {
    const oldest = session(freshId("a"), "2026-07-24T10:00:00Z");
    const middle = session(freshId("b"), "2026-07-24T11:00:00Z");
    const newest = session(freshId("c"), "2026-07-24T12:00:00Z");
    const ordered = inVoiceQueueOrder([newest, oldest, middle]);
    expect(ordered.map((s) => s.sessionId)).toEqual([oldest.sessionId, middle.sessionId, newest.sessionId]);
  });

  it("drops a listened-but-unhandled session to the bottom of the queue", () => {
    const heard = session(freshId("heard"), "2026-07-24T10:00:00Z");
    const waiting = session(freshId("waiting"), "2026-07-24T11:00:00Z");
    // The oldest session was listened to and left unhandled AFTER the other one started waiting: it
    // re-enters the line at the back.
    touchQueue(heard.sessionId ?? "", Date.parse("2026-07-24T12:00:00Z"));
    const ordered = inVoiceQueueOrder([heard, waiting]);
    expect(ordered.map((s) => s.sessionId)).toEqual([waiting.sessionId, heard.sessionId]);
  });

  it("a NEW turn after the touch puts the session back in line by its fresh wait time", () => {
    // Listened at 12:00, but the session came back needing you again at 13:00: the newer
    // needsYouSince governs, so it queues as a fresh arrival, not as its old touched self.
    const again = session(freshId("again"), "2026-07-24T13:00:00Z");
    const older = session(freshId("older"), "2026-07-24T12:30:00Z");
    touchQueue(again.sessionId ?? "", Date.parse("2026-07-24T12:00:00Z"));
    const ordered = inVoiceQueueOrder([again, older]);
    expect(ordered.map((s) => s.sessionId)).toEqual([older.sessionId, again.sessionId]);
  });

  it("a session with no needsYouSince and no touch sorts to the bottom", () => {
    const placed = session(freshId("placed"), "2026-07-24T10:00:00Z");
    const unplaced = session(freshId("unplaced"), null);
    const ordered = inVoiceQueueOrder([unplaced, placed]);
    expect(ordered.map((s) => s.sessionId)).toEqual([placed.sessionId, unplaced.sessionId]);
  });

  it("a touched session with no needsYouSince is still placeable (by its touch)", () => {
    const touched = session(freshId("touched"), null);
    const later = session(freshId("later"), "2026-07-24T12:00:00Z");
    touchQueue(touched.sessionId ?? "", Date.parse("2026-07-24T10:00:00Z"));
    const ordered = inVoiceQueueOrder([later, touched]);
    expect(ordered.map((s) => s.sessionId)).toEqual([touched.sessionId, later.sessionId]);
  });

  it("breaks ties by createdAt so equal stamps never jitter", () => {
    const first = session(freshId("t1"), "2026-07-24T10:00:00Z", "2026-07-24T01:00:00Z");
    const second = session(freshId("t2"), "2026-07-24T10:00:00Z", "2026-07-24T02:00:00Z");
    const ordered = inVoiceQueueOrder([second, first]);
    expect(ordered.map((s) => s.sessionId)).toEqual([first.sessionId, second.sessionId]);
  });
});

describe("touchQueue / queueTouchMs", () => {
  it("round-trips the touch stamp", () => {
    const sid = freshId("round");
    expect(queueTouchMs(sid)).toBe(0);
    touchQueue(sid, 1234567890);
    expect(queueTouchMs(sid)).toBe(1234567890);
  });

  it("ignores an empty session id", () => {
    touchQueue("", 42);
    expect(queueTouchMs("")).toBe(0);
  });
});

describe("auto-speak setting", () => {
  it("defaults off, persists on, and clears off", () => {
    expect(getAutoSpeak()).toBe(false);
    setAutoSpeak(true);
    expect(getAutoSpeak()).toBe(true);
    setAutoSpeak(false);
    expect(getAutoSpeak()).toBe(false);
  });
});
