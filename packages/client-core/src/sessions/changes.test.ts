import { describe, expect, it } from "vitest";
import { changesBadge, changesTitle, uncommittedCount } from "./changes";
import type { SessionDto } from "../api/client";

// The badge for a session's uncommitted work. The property worth pinning is the one that is invisible
// once it is wrong: UNKNOWN AND CLEAN BOTH RENDER NOTHING, but they are different answers, and a client
// must never turn "the git probe failed" into a clean tree.
const session = (uncommitted?: unknown): SessionDto =>
  ({ sessionId: "s1", ...(uncommitted === undefined ? {} : { uncommittedCount: uncommitted }) }) as SessionDto;

describe("uncommittedCount", () => {
  it("reads a positive count", () => {
    expect(uncommittedCount(session(12))).toBe(12);
  });

  it("reads a verified-clean tree as zero", () => {
    expect(uncommittedCount(session(0))).toBe(0);
  });

  it("reads null, undefined and a missing field as unknown", () => {
    expect(uncommittedCount(session(null))).toBeNull();
    expect(uncommittedCount(session())).toBeNull();
  });

  it("coerces the numeric-string form the serializer can emit", () => {
    expect(uncommittedCount(session("7"))).toBe(7);
  });

  it("treats an unparseable value as unknown, never as zero", () => {
    // A zero here would be the client inventing a clean tree out of a value it could not read.
    expect(uncommittedCount(session("banana"))).toBeNull();
    expect(uncommittedCount(session(-3))).toBeNull();
  });
});

describe("changesBadge", () => {
  it("shows the count on a dirty tree", () => {
    expect(changesBadge(session(12))).toBe("12 chg");
    expect(changesBadge(session(1))).toBe("1 chg");
  });

  it("shows nothing on a clean tree", () => {
    expect(changesBadge(session(0))).toBeNull();
  });

  it("shows nothing when the count is unknown", () => {
    // Including the older-Director case, where the field simply is not on the wire.
    expect(changesBadge(session(null))).toBeNull();
    expect(changesBadge(session())).toBeNull();
  });
});

describe("changesTitle", () => {
  it("spells out what the number means, and gets the singular right", () => {
    expect(changesTitle(session(1))).toBe("1 uncommitted file in this session's working tree");
    expect(changesTitle(session(4))).toBe("4 uncommitted files in this session's working tree");
  });

  it("has no tooltip when there is no badge", () => {
    expect(changesTitle(session(0))).toBeNull();
    expect(changesTitle(session())).toBeNull();
  });
});
